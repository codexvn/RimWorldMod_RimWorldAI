using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
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

            // Skills — Assemblies/Skills（和 DLL 同目录）
            var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                ? Path.Combine(modRoot, settings!.SkillsDir)
                : Path.GetFullPath(Path.Combine(modRoot, "Skills"));
            InternalToolRegistry.Instance.LoadSkills(skillsDir);
            InternalToolRegistry.Instance.InitializeSkillTools();
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

            _ccb = new CcbManager(ccbDir, sessionDir, ccbPort, mcpPort, agentMcpPort, null, ccbToken);
            if (settings == null || settings.CCBAutoStart)
            {
                if (_ccb.Start()) await _ccb.WaitReadyAsync(15000);
            }

            // 连接 CCB WebSocket（事件转发 + Agent 对话共用）
            var ccbWsUrl = $"ws://{settings?.CCBRemoteHost ?? "127.0.0.1"}:{settings?.CCBRemotePort ?? 19999}";
            _ccbWs = new CcbWebSocket(ccbWsUrl, settings?.CCBAuthToken ?? "");
            if (await _ccbWs.ConnectAsync())
            {
                EventForwarder.SetCcbSocket(_ccbWs);
                Log.Message("[agent-mod] CCB WebSocket 已连接");
            }
            else Log.Warning("[agent-mod] CCB WebSocket 连接失败，事件转发不可用");

            _mcp = new McpClient($"http://localhost:{mcpPort}");
            _mcp.OnGameEvent += evt =>
            {
                if (evt.Severity == "Critical" && evt.Category == "Combat")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Combat);
                else if (evt.Severity != "Critical")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Overseer);
            };
            _mcp.StartSse();

            _ctx = new ContextBuilder(_mcp);
            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (!_initialized) return;

            // CCB 崩溃重启
            _ccb?.TickAndRestart();

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

            try
            {
                // 1. 获取世界状态
                var summary = await _mcp.CallTool("get_world_summary");
                var input = ParseToSchedulerInput(summary);
                Scheduler.Tick(input);
                AgentOrchestrator.GameDay = input.CurrentTick / 60000;

            }
            catch { /* 轮询失败不影响主循环，下次重试 */ }

            var currentTick = Environment.TickCount;
            foreach (var config in AgentConfigs.All)
            {
                if (config.Name == "combat") continue;
                if (!AgentOrchestrator.IsSleeping(config.Name)) continue;

                bool shouldWake = false;
                if (config.IntervalGameHours > 0 && Scheduler.ShouldWake(config.Name, config.IntervalGameHours, currentTick))
                    shouldWake = true;
                if (config.TriggerDaily && AgentOrchestrator.IsNewDay(config.Name))
                    shouldWake = true;

                if (shouldWake)
                {
                    AgentOrchestrator.BeginAgent(config.Name);
                    Log.Message($"[agent-mod] 唤醒 {config.Name} (Load={Scheduler.LoadScore})");

                    var prompt = await _ctx.BuildAsync(config);
                    await RunAgentSession(config, prompt);

                    AgentOrchestrator.EndAgent(config.Name);
                    Log.Message($"[agent-mod] {config.Name} 休眠");
                }
            }
        }

        private async Task RunAgentSession(AgentConfig config, string prompt)
        {
            if (_ccbWs == null || !_ccbWs.IsReady) { Log.Warning($"[agent-mod] {config.Name} CCB 未就绪"); return; }
            if (_mcp == null) return;

            var tcs = new TaskCompletionSource<bool>();

            void OnSessionResult(string subtype, string? _) { Log.Message($"[agent-mod] {config.Name} 结束: {subtype}"); tcs.TrySetResult(true); }
            async void OnSessionToolUse(string toolId, string toolName, JsonElement? input)
            {
                await ToolDispatcher.HandleAsync(_ccbWs, _mcp!, toolId, toolName, input,
                    msg => Log.Message($"[agent-mod] {msg}"));
            }

            _ccbWs.OnResult += OnSessionResult;
            _ccbWs.OnToolUse += OnSessionToolUse;
            try
            {
                await _ccbWs.SendChat(prompt);
                var timeout = Task.Delay(config.Name == "combat" ? 300000 : 120000);
                await Task.WhenAny(tcs.Task, timeout);
            }
            finally
            {
                _ccbWs.OnResult -= OnSessionResult;
                _ccbWs.OnToolUse -= OnSessionToolUse;
            }

            try { MemoryManager.Append(config.Name, new MemoryEntry { Day = AgentOrchestrator.GameDay, Insight = $"Load={Scheduler.LoadScore}({Scheduler.Mode})", Type = "session" }); } catch { }
        }

        private static SchedulerInput ParseToSchedulerInput(string text)
        {
            var input = new SchedulerInput { CurrentTick = Environment.TickCount };
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.Contains("殖民者") && t.Contains("|")) { int.TryParse(t.Split('|')[1].Trim(), out input.ColonistCount); }
                else if (t.Contains("食物") && t.Contains("天")) { float.TryParse(t.Split('|')[1].Trim().Replace("天", ""), out var f); input.FoodDays = f; }
                else if (t.Contains("敌人")) { int.TryParse(t.Split('|')[1].Trim(), out input.EnemyCount); }
            }
            return input;
        }

        public override void ExposeData() { base.ExposeData(); }
    }
}
