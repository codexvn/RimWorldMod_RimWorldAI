using System;
using System.IO;
using System.Threading.Tasks;
using Ccb = RimWorldAgent.Core.CcbManager.CcbManager;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Agent 引擎配置 — 构造后传 InitAsync</summary>
    public class AgentEngineConfig
    {
        public string ProjectPath { get; set; } = "";
        public string? SkillsDir { get; set; }
        public string McpUrl { get; set; } = "http://localhost:9877";
        public int McpPort { get; set; } = 9877;
        public int AgentMcpPort { get; set; } = 9878;
        public int CcbPort { get; set; } = 19999;
        public string CcbWsUrl { get; set; } = "ws://127.0.0.1:19999";
        public string? CcbToken { get; set; }
        public string? ModelName { get; set; }
        public bool CcbAutoStart { get; set; } = true;
        public bool CcbAutoInstall { get; set; } = true;
        public string CcbDir { get; set; } = "";
        public string PlanSpeed { get; set; } = "paused";
        public bool WaitForGame { get; set; } = false;
        public long TokenBudgetLimit { get; set; }
        public string ThinkingEffort { get; set; } = "medium";
        public int MaxThinkingTokens { get; set; }
    }

    /// <summary>Agent 引擎 — CCB 生命周期 + WS + MCP + 调度循环。EXE/MOD 共享。</summary>
    public class AgentEngine : IDisposable
    {
        private readonly AgentEngineConfig _cfg;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly Action<string> _logDebug;
        private Ccb? _ccb;
        private CcbWebSocket? _ccbWs;
        private McpClient? _mcp;
        private ContextBuilder? _ctx;
        private bool _initialized;
        private int _lastDialogCheckTick;
        private SimpleMspServer.McpServiceHost? _agentHost;

        public CcbWebSocket? CcbWs => _ccbWs;
        public bool IsReady => _initialized && _mcp != null;

        public AgentEngine(AgentEngineConfig cfg, Action<string>? logInfo = null, Action<string>? logError = null, Action<string>? logDebug = null)
        {
            _cfg = cfg;
            _logInfo = logInfo ?? (msg => { });
            _logError = logError ?? (msg => { });
            _logDebug = logDebug ?? (msg => { });
        }

        /// <summary>完整启动流程：Skills → AgentMCP → npm install → CCB spawn → WS → MCP → SSE</summary>
        public async Task<bool> InitAsync()
        {
            if (_initialized) return true;
            _initialized = true;

            CoreLog.OnInfo = _logInfo;
            CoreLog.OnError = _logError;
            CoreLog.OnDebug = _logDebug;

            // Session 目录
            Directory.CreateDirectory(_cfg.ProjectPath);
            SessionStore.ProjectPath = _cfg.ProjectPath;

            // Data 层 — Token 持久化
            TokenStore.Instance = new LocalFileTokenStore();

            // Skills
            var skillsDir = _cfg.SkillsDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            InternalToolRegistry.Instance.LoadSkills(skillsDir);

            // Agent MCP Server
            _agentHost = new SimpleMspServer.McpServiceHost(_cfg.AgentMcpPort,
                log: new SimpleMspServer.DelegateMspLog(_logInfo));
            _agentHost.RegisterProvider(InternalToolRegistry.Instance);
            _agentHost.Start();
            _logInfo($"[AgentEngine] AgentMCP :{_cfg.AgentMcpPort}");

            // MCP 客户端 — 必须在 CCB 之前连接，确保游戏工具列表可用
            _mcp = new McpClient(_cfg.McpUrl);

            // 等待 MCP 服务就绪（EXE 模式下 Agent 可能先于游戏启动）
            if (_cfg.WaitForGame)
            {
                _logInfo("[AgentEngine] 等待游戏 MCP 服务就绪...");
                while (true)
                {
                    try { await _mcp.CallTool("check_map_loaded"); break; }
                    catch (Exception ex) { _logInfo($"[AgentEngine] 游戏尚未就绪: {ex.Message}，3s 后重试..."); await Task.Delay(3000); }
                }
                _logInfo("[AgentEngine] 游戏 MCP 已连接");
            }

            // 游戏事件订阅 + 游戏工具代理 → Agent MCP（必须在 CCB 之前完成）
            AgentLoop.WireEvents(_mcp);
            var proxy = new ProxyToolProvider(_mcp);
            await proxy.RefreshToolsAsync();
            _agentHost.RegisterProvider(proxy);
            _logInfo($"[AgentEngine] 游戏工具代理已注册");

            // CCB 子进程 + WS — 在所有 MCP 服务就绪后启动
            var ccbReady = false;
            if (!string.IsNullOrEmpty(_cfg.CcbDir) && Directory.Exists(_cfg.CcbDir))
            {
                if (_cfg.CcbAutoInstall && !CompanionInstaller.IsInstalled(_cfg.CcbDir))
                {
                    _logInfo("[AgentEngine] CCB: npm install...");
                    await CompanionInstaller.InstallAsync(_cfg.CcbDir);
                }

                _ccb = new Ccb(_cfg.CcbDir, _cfg.ProjectPath, _cfg.CcbPort,
                    mcpPort: _cfg.McpPort, agentMcpPort: _cfg.AgentMcpPort,
                    ccbToken: _cfg.CcbToken, modelName: _cfg.ModelName);
                if (_cfg.CcbAutoStart)
                {
                    if (_ccb.Start()) { await _ccb.WaitReadyAsync(15000); _logInfo("[AgentEngine] CCB: 就绪"); }
                }

                if (_ccb.IsReady)
                {
                    _ccbWs = new CcbWebSocket(_cfg.CcbWsUrl, _cfg.CcbToken ?? "")
                    {
                        BudgetLimit = _cfg.TokenBudgetLimit,
                        ThinkingEffort = _cfg.ThinkingEffort,
                        MaxThinkingTokens = _cfg.MaxThinkingTokens
                    };
                    if (await _ccbWs.ConnectAsync())
                    {
                        AgentLoop.WireCcbStatus(_ccbWs);
                        _logInfo("[AgentEngine] CCB WS: 已连接");
                        ccbReady = true;
                    }
                    else
                    {
                        _logError("[AgentEngine] CCB WS: 连接失败");
                        _ccbWs.Dispose();
                        _ccbWs = null;
                    }
                }
            }

            if (!ccbReady) _logInfo("[AgentEngine] CCB: 未就绪 (事件转发不可用)");

            GamePaceController.PlanSpeed = _cfg.PlanSpeed;
            _ctx = new ContextBuilder(_mcp);

            return ccbReady;
        }

        /// <summary>同步维护：CCB 崩溃重启 + WS 重连。MOD 每帧调用，EXE 循环中调用。</summary>
        public void Tick()
        {
            if (!_initialized) return;

            if (_ccb != null)
            {
                _ccb.TickAndRestart();
                if (_ccb.WasRestarted)
                {
                    _ccb.WasRestarted = false;
                    _logInfo("[AgentEngine] CCB 进程已重启，重连 WS...");
                    _ccbWs?.Dispose();
                    _ccbWs = new CcbWebSocket(_cfg.CcbWsUrl, _cfg.CcbToken ?? "")
                    {
                        BudgetLimit = _cfg.TokenBudgetLimit,
                        ThinkingEffort = _cfg.ThinkingEffort,
                        MaxThinkingTokens = _cfg.MaxThinkingTokens
                    };
                    _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                    {
                        if (t.Result) AgentLoop.WireCcbStatus(_ccbWs!);
                        else { _ccbWs?.Dispose(); _ccbWs = null; }
                    });
                }
            }

            // WS 被动断开自动重连（CcbWebSocket 内部 ScheduleReconnect 处理）
            if (_ccbWs != null && _ccbWs.State == CcbClientState.Disconnected)
            {
                _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                {
                    if (t.Result) AgentLoop.WireCcbStatus(_ccbWs!);
                });
            }
        }

        /// <summary>单 Agent 调度循环</summary>
        public async Task TickAsync()
        {
            if (_mcp == null || _ctx == null || _ccbWs == null || !_ccbWs.IsReady) return;

            var currentTick = AgentOrchestrator.GameTick;

            // 定时弹框扫描（每 2500 tick ≈ 60s 游戏时间）
            if (currentTick - _lastDialogCheckTick >= 2500)
            {
                _lastDialogCheckTick = currentTick;
                try
                {
                    var dialogsResult = await _mcp.CallTool("get_open_dialogs");
                    if (!dialogsResult.Contains("没有打开") && !dialogsResult.Contains("没有可交互"))
                    {
                        AgentOrchestrator.RequestInterrupt("弹框提示 — 请调用 get_open_dialogs 查看并处理");
                        _logInfo("[AgentEngine] 检测到弹框，已发送中断");
                    }
                }
                catch (Exception ex) { _logInfo($"[AgentEngine] 弹框检测失败: {ex.Message}"); }
            }

            // 优先级 1: 中断请求 + Agent 运行中 → 等待 AgentLoop 中 SendAbort 处理
            if (AgentOrchestrator.InterruptRequested && AgentOrchestrator.IsRunning)
                return;

            // 优先级 2: 中断请求 + Agent 空闲 → 立即启动新会话
            if (AgentOrchestrator.InterruptRequested && !AgentOrchestrator.IsRunning)
            {
                AgentOrchestrator.InterruptRequested = false;
                await RunAgent(isInterrupted: true);
                return;
            }

            // 优先级 3: 每日 PLAN 模式
            if (!AgentOrchestrator.IsRunning && AgentOrchestrator.ShouldMorningReport())
            {
                AgentOrchestrator.EnterPlanPhase();
                if (AgentOrchestrator.PaceController == null)
                    AgentOrchestrator.PaceController = new GamePaceController();
                await AgentOrchestrator.PaceController.PauseForPlanning(_mcp, GamePaceController.PlanSpeed);
                await RunAgent(isPlan: true);
                return;
            }

            // 优先级 4: 定期 ACT 检查
            if (!AgentOrchestrator.IsRunning
                && Scheduler.ShouldWake(AgentConfigs.Default.IntervalGameHours, currentTick))
            {
                await RunAgent(isPlan: false);
                return;
            }
        }

        private async Task RunAgent(bool isPlan = false, bool isInterrupted = false)
        {
            GamePaceController.ShouldSkipResume = null;
            AgentOrchestrator.BeginSession();
            _logInfo($"[AgentEngine] 唤醒 commander (Load={Scheduler.LoadScore}, Plan={isPlan}, Interrupted={isInterrupted})");

            await _ccbWs!.SendEvent("agent.status", new { text = AgentOrchestrator.StatusText });

            var prompt = await _ctx!.BuildAsync(isInterrupted: isInterrupted);
            await AgentLoop.RunSessionAsync(prompt, _mcp!, _ccbWs);

            AgentOrchestrator.EndSession();
            _logInfo("[AgentEngine] commander 休眠");
        }

        public void Dispose()
        {
            _ccbWs?.Dispose();
            _ccbWs = null;
            _ccb?.Dispose();
            _ccb = null;
            _mcp?.Dispose();
            _mcp = null;
            _agentHost?.Stop();
            _agentHost = null;
        }
    }
}
