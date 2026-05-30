using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using RimWorldAgent.Core.Mcp;
using Verse;

namespace RimWorldAgent
{
    public class GameComponent_RimWorldAgent : GameComponent
    {
        private McpClient? _mcp;
        private CcbManager? _ccb;
        private CcbWebSocket? _ccbWs;
        private ContextBuilder? _ctx;
        private SimpleMspServer.McpServiceHost? _agentHost;
        private bool _initialized;
        private int _lastTick;

        public GameComponent_RimWorldAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            if (_initialized) return;
            _initialized = true;

            var settings = RimWorldAgentMod.Instance?.Settings;
            if (settings != null && !settings.AgentAutoRun) return;

            CoreLog.OnInfo = msg => Log.Message($"[agent-core] {msg}");
            CoreLog.OnError = msg => Log.Error($"[agent-core] {msg}");

            var modRoot = Path.GetDirectoryName(typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var defaultSessionDir = Path.Combine(modRoot, "claude-sessions", "rimworld-agent");
            var sessionDir = !string.IsNullOrEmpty(settings?.SessionDir)
                ? Path.Combine(modRoot, settings!.SessionDir)
                : defaultSessionDir;
            Directory.CreateDirectory(sessionDir);
            TaskBoard.SessionDir = sessionDir;

            // Tick 提供者 — MOD 模式从 RimWorld 获取游戏 tick
            TodoStore.TickProvider = () => Find.TickManager?.TicksAbs ?? 0;

            // Skills — Assemblies/Skills（和 DLL 同目录）
            var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                ? Path.Combine(modRoot, settings!.SkillsDir)
                : Path.GetFullPath(Path.Combine(modRoot, "Skills"));
            InternalToolRegistry.Instance.LoadSkills(skillsDir);
            Log.Message($"[agent-mod] Skills 加载: {skillsDir}");

            // Agent MCP Server — 暴露内部 Tool 给 CCB（端口从设置读取）
            var agentMcpPort = settings?.AgentMcpPort ?? 9878;
            _agentHost = new SimpleMspServer.McpServiceHost(agentMcpPort, log: new SimpleMspServer.DelegateMspLog(Verse.Log.Message));
            _agentHost.RegisterProvider(InternalToolRegistry.Instance);
            _agentHost.Start();
            Log.Message($"[agent-mod] AgentMcpServer :{agentMcpPort} 启动");

            // CCB 子进程 — Assemblies/cc-companion（和 DLL 同目录）
            var ccbPort = settings?.CCBPort ?? 19999;
            var mcpPort = settings?.McpPort ?? 9877;
            var ccbToken = settings?.CCBAuthToken;
            var ccbDir = Path.GetFullPath(Path.Combine(modRoot, "cc-companion"));

            // 自动安装 Claude Code 依赖（npm install）
            if ((settings == null || settings.CCBAutoInstall) && !CompanionInstaller.IsInstalled(ccbDir))
            {
                Log.Message($"[agent-mod] Claude Code 依赖未安装，npm install...");
                await CompanionInstaller.InstallAsync(ccbDir);
            }

            _ccb = new CcbManager(ccbDir, sessionDir, ccbPort, mcpPort, agentMcpPort, null, ccbToken, settings?.CCBModelName);
            if (settings == null || settings.CCBAutoStart)
            {
                if (_ccb.Start()) await _ccb.WaitReadyAsync(15000);
            }

            // 连接 CCB WebSocket（事件转发 + Agent 对话共用）
            var ccbWsUrl = $"ws://{settings?.CCBRemoteHost ?? "127.0.0.1"}:{settings?.CCBRemotePort ?? 19999}";
            _ccbWs = new CcbWebSocket(ccbWsUrl, settings?.CCBAuthToken ?? "")
            {
                BudgetLimit = settings?.TokenBudgetLimit ?? 0,
                ThinkingEffort = settings?.CCBThinkingEffort ?? "medium",
                MaxThinkingTokens = settings?.CCBMaxThinkingTokens ?? 0
            };
            if (await _ccbWs.ConnectAsync())
            {
                EventForwarder.SetCcbSocket(_ccbWs);
                AgentLoop.WireCcbStatus(_ccbWs);
                Log.Message("[agent-mod] CCB WebSocket 已连接");
            }
            else Log.Warning("[agent-mod] CCB WebSocket 连接失败，事件转发不可用");

            // TODO 变更 → 推送到 Companion
            TodoStore.OnChanged += () =>
            {
                if (_ccbWs?.IsReady == true)
                {
                    var items = TodoStore.Query(null);
                    _ccbWs.SendEvent("todo-state", new
                    {
                        todoItems = items.Select(i => new
                        {
                            id = i.Id, description = i.Description, priority = i.Priority,
                            status = i.Status, createdAtTick = i.CreatedAtTick
                        }).ToArray()
                    });
                }
            };

            _mcp = new McpClient($"http://localhost:{mcpPort}");
            AgentLoop.WireEvents(_mcp);
            _mcp.StartSse();

            // 首次连接通知 Companion，触发 Agent 开始工作
            EventForwarder.SendGameConnected();

            // Plan/Act 阶段：L3 危险事件暂停时不自动恢复游戏
            GamePaceController.ShouldSkipResume = () => EventForwarder.DangerPaused;
            GamePaceController.PlanSpeed = settings?.PlanSpeed ?? "paused";

            _ctx = new ContextBuilder(_mcp);
            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (!_initialized) return;

            // CCB 崩溃重启 + WS 重连
            if (_ccb != null)
            {
                _ccb.TickAndRestart();
                if (_ccb.WasRestarted)
                {
                    _ccb.WasRestarted = false;
                    Log.Message("[agent-mod] CCB 进程已重启，重连 WS...");
                    _ = _ccbWs?.ConnectAsync();
                }
            }

            // 事件转发（每帧）
            EventForwarder.Tick();

            if (_mcp == null || _ctx == null) return;
            if (Find.CurrentMap == null) return;

            _lastTick++;
            var settings = RimWorldAgentMod.Instance?.Settings;
            var tickThreshold = (settings?.LoopIntervalMs ?? 10000) / 16;
            if (tickThreshold < 1) tickThreshold = 600;
            if (_lastTick < tickThreshold) return;
            _lastTick = 0;
            _ = AgentTickAsync();
        }

        private async Task AgentTickAsync()
        {
            if (_mcp == null || _ctx == null) return;

            // Scheduler 已由 SSE game/world-state 自动更新，无需 HTTP 轮询
            var currentTick = AgentOrchestrator.GameTick;

            // Overseer — 定时唤醒（唯一入口，其他角色由 Overseer 委托）
            if (AgentOrchestrator.IsSleeping("overseer"))
            {
                bool shouldWake = Scheduler.ShouldWake("overseer", AgentConfigs.Overseer.IntervalGameHours, currentTick)
                    || AgentOrchestrator.IsNewDay("overseer");
                if (shouldWake)
                {
                    await RunAgentWithSwitchSupport(AgentConfigs.Overseer);
                }
            }

            // Combat Agent — L3 Critical 事件驱动唤醒
            if (_ccbWs != null && _ccbWs.IsReady
                && AgentOrchestrator.IsSleeping("combat")
                && AgentOrchestrator.HasPendingEvents("combat"))
            {
                await RunAgentWithSwitchSupport(AgentConfigs.Combat);
            }
        }

        /// <summary>运行 Agent 会话，结束后检查 switch_agent 请求并自动切换</summary>
        private async Task RunAgentWithSwitchSupport(AgentConfig config)
        {
            if (_mcp == null || _ctx == null || _ccbWs == null) return;

            AgentOrchestrator.NextAgentRequest = null;
            AgentOrchestrator.BeginAgent(config.Name);
            Log.Message($"[agent-mod] 唤醒 {config.Name} (Load={Scheduler.LoadScore})");

            var prompt = await _ctx.BuildAsync(config);
            await AgentLoop.RunSessionAsync(config, prompt, _mcp, _ccbWs);

            AgentOrchestrator.EndAgent(config.Name);
            Log.Message($"[agent-mod] {config.Name} 休眠");

            // 检查 switch_agent 请求
            var nextAgent = AgentOrchestrator.NextAgentRequest;
            AgentOrchestrator.NextAgentRequest = null;
            if (!string.IsNullOrEmpty(nextAgent) && AgentOrchestrator.IsSleeping(nextAgent))
            {
                var nextConfig = AgentConfigs.Get(nextAgent);
                if (nextConfig != null)
                {
                    Log.Message($"[agent-mod] switch_agent → {nextAgent}");
                    await RunAgentWithSwitchSupport(nextConfig);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            TodoManager.ExposeData();
        }
    }
}
