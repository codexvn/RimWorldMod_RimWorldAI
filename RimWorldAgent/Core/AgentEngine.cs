using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Ccb = RimWorldAgent.Core.CcbManager.CcbManager;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using RimWorldAgent.Core.Mcp;
using RimWorldAgent.Core.models;

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
        public int CcbPort { get; set; } = 19998;
        public string CcbWsUrl { get; set; } = "ws://127.0.0.1:19998";
        public string? CcbToken { get; set; }
        public string? ModelName { get; set; }
        public bool CcbAutoStart { get; set; } = true;
        public bool CcbAutoInstall { get; set; } = true;
        public string CcbDir { get; set; } = "";
        public string PlanSpeed { get; set; } = "paused";
        public bool WaitForGame { get; set; } = false;
        public long TokenBudgetLimit { get; set; }
        public string ThinkingMode { get; set; } = "default";
        public string ThinkingEffort { get; set; } = "medium";
        public int MaxThinkingTokens { get; set; }
    }

    /// <summary>Agent 引擎 — CCB 生命周期 + WS + MCP + 调度循环。EXE/MOD 共享。</summary>
    public class AgentEngine : IDisposable
    {
        private readonly AgentEngineConfig _cfg;
        private readonly IDbStore _dbStore;
        private readonly IGameStateProvider _gameState;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly Action<string> _logDebug;
        private readonly Action<string> _logWarn;
        private Ccb? _ccb;
        private CcbWebSocket? _ccbWs;
        private McpClient? _mcp;
        private ContextBuilder? _ctx;
        private bool _initialized;
        private int _lastDialogCheckTick;
        private int _lastStatusCheckTick;
        private int _pauseStartMs;
        private int _lastPauseRemindMs;
        private SimpleMspServer.McpServiceHost? _agentHost;

        public CcbWebSocket? CcbWs => _ccbWs;
        public bool IsReady => _initialized && _mcp != null;

        public AgentEngine(AgentEngineConfig cfg, IDbStore dbStore, IGameStateProvider gameState,
            Action<string>? logInfo = null, Action<string>? logError = null, Action<string>? logDebug = null, Action<string>? logWarn = null)
        {
            _cfg = cfg;
            _dbStore = dbStore;
            _gameState = gameState;
            _logInfo = logInfo ?? (msg => { });
            _logError = logError ?? (msg => { });
            _logDebug = logDebug ?? (msg => { });
            _logWarn = logWarn ?? (msg => { });
        }

        /// <summary>完整启动流程：Skills → AgentMCP → npm install → CCB spawn → WS → MCP → SSE</summary>
        public async Task<bool> InitAsync()
        {
            if (_initialized) return true;
            _initialized = true;

            CoreLog.OnInfo = _logInfo;
            CoreLog.OnError = _logError;
            CoreLog.OnDebug = _logDebug;
            CoreLog.OnWarn = _logWarn;

            // Session 目录
            Directory.CreateDirectory(_cfg.ProjectPath);
            SessionStore.ProjectPath = _cfg.ProjectPath;

            // Data 层 — 注入 IDbStore
            TokenUsageTracker.Db = _dbStore;
            AgentLoop.BudgetLimit = _cfg.TokenBudgetLimit;

            // Skills
            var skillsDir = _cfg.SkillsDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            InternalToolRegistry.Instance.LoadSkills(skillsDir);

            // Agent MCP Server
            _agentHost = new SimpleMspServer.McpServiceHost(_cfg.AgentMcpPort,
                log: new SimpleMspServer.DelegateMspLog(_logInfo));
            _agentHost.RegisterProvider(InternalToolRegistry.Instance);
            _agentHost.Start();
            _logInfo($"[AgentEngine] AgentMCP :{_cfg.AgentMcpPort}");

            // MCP 客户端 — 连接游戏 MCP Server，等待就绪后再代理工具
            _mcp = new McpClient(_cfg.McpUrl);

            _logInfo("[AgentEngine] 等待游戏 MCP 服务就绪...");
            while (true)
            {
                try
                {
                    var tools = await _mcp.ListToolsAsync();
                    _logInfo($"[AgentEngine] 游戏 MCP 已连接 ({tools.Count} 工具)");
                    break;
                }
                catch (Exception ex) { _logInfo($"[CCGUI_DEBUG] AgentEngine MCP 就绪检查失败: {ex.GetType().Name}: {ex.Message}"); if (ex.InnerException != null) _logInfo($"[CCGUI_DEBUG] AgentEngine MCP InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}"); _logInfo($"[AgentEngine] 游戏 MCP 尚未就绪，3s 后重试..."); await Task.Delay(3000); }
            }

            // 游戏事件订阅 + 游戏工具代理 → Agent MCP（必须在 CCB 之前完成）
            AgentLoop.WireEvents(_mcp);
            _logInfo("[AgentEngine] 开始代理游戏工具...");
            var proxy = new ProxyToolProvider(_mcp);
            await proxy.RefreshToolsAsync();
            _agentHost.RegisterProvider(proxy);
            _logInfo("[AgentEngine] 游戏工具代理已注册");

            // CCB 子进程 + WS — 在所有 MCP 服务就绪后启动
            var ccbReady = false;
            if (!string.IsNullOrEmpty(_cfg.CcbDir) && Directory.Exists(_cfg.CcbDir))
            {
                _logInfo($"[AgentEngine] CCB dir={_cfg.CcbDir}, 开始启动...");
                if (_cfg.CcbAutoInstall && !CompanionInstaller.IsInstalled(_cfg.CcbDir))
                {
                    _logInfo("[AgentEngine] CCB: npm install...");
                    var ok = await CompanionInstaller.InstallAsync(_cfg.CcbDir);
                    _logInfo(ok
                        ? $"[AgentEngine] CCB: npm install 完成"
                        : $"[AgentEngine] CCB: npm install 失败 — {CompanionInstaller.InstallStatus}");
                }

                _ccb = new Ccb(_cfg.CcbDir, _cfg.ProjectPath, _cfg.CcbPort,
                    mcpPort: _cfg.McpPort, agentMcpPort: _cfg.AgentMcpPort,
                    ccbToken: _cfg.CcbToken, modelName: _cfg.ModelName,
                    budgetLimit: _cfg.TokenBudgetLimit, budgetAction: "Block");
                if (_cfg.CcbAutoStart)
                {
                    _logInfo("[AgentEngine] 调用 _ccb.Start()...");
                    var started = _ccb.Start();
                    _logInfo($"[AgentEngine] _ccb.Start() = {started}");
                    if (started)
                    {
                        _logInfo("[AgentEngine] 等待 CCB 就绪 (最多 15s)...");
                        await _ccb.WaitReadyAsync(15000);
                        _logInfo($"[AgentEngine] WaitReady 完成, _ccb.IsReady={_ccb.IsReady}");
                    }
                }

                if (_ccb.IsReady)
                {
                    _logInfo("[AgentEngine] 开始 WS 连接...");
                    _ccbWs = new CcbWebSocket(_cfg.CcbWsUrl, _cfg.CcbToken ?? "")
                    {
                        ThinkingMode = _cfg.ThinkingMode,
                        ThinkingEffort = _cfg.ThinkingEffort,
                        MaxThinkingTokens = _cfg.MaxThinkingTokens
                    };
                    var wsOk = await _ccbWs.ConnectAsync();
                    _logInfo($"[AgentEngine] WS ConnectAsync = {wsOk}");
                    if (wsOk)
                    {
                        AgentOrchestrator.CcbWs = _ccbWs;
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
                else
                {
                    _logError("[AgentEngine] CCB: 启动失败或超时, 跳过 WS 连接");
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
                        ThinkingMode = _cfg.ThinkingMode,
                        ThinkingEffort = _cfg.ThinkingEffort,
                        MaxThinkingTokens = _cfg.MaxThinkingTokens
                    };
                    _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                    {
                        if (t.Result) AgentOrchestrator.CcbWs = _ccbWs!;
                        else { _ccbWs?.Dispose(); _ccbWs = null; }
                    });
                }
            }

            // WS 被动断开自动重连（CcbWebSocket 内部 ScheduleReconnect 处理）
            if (_ccbWs != null && _ccbWs.State == CcbClientState.Disconnected)
            {
                _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                {
                    if (t.Result) AgentOrchestrator.CcbWs =(_ccbWs!);
                });
            }
        }

        public async Task TickAsync()
        {
            if (_mcp == null || _ctx == null || _ccbWs == null || !_ccbWs.IsReady) return;

            await _gameState.SyncGameStatusAsync();
            var currentTick = _gameState.GameTick;
            AgentOrchestrator.GameTick = currentTick;

            // 优先级 0: 冷启动 — 首次连接后检查游戏就绪，发送初始 prompt
            if (!AgentOrchestrator.IsRunning && !AgentLoop.HasEverSent)
            {
                try
                {
                    var speedResult = await _mcp.CallTool("get_game_speed");
                    _logInfo($"[AgentEngine] 冷启动检测: get_game_speed={speedResult.Trim()}");
                    if (!string.IsNullOrEmpty(speedResult) && speedResult != "error")
                    {
                        await _gameState.SyncGameStatusAsync();
                        _logInfo($"[AgentEngine] 游戏已就绪 (Tick={_gameState.GameTick})");
                        await RunAgent(isPlan: false);
                        _logInfo($"[AgentEngine] 冷启动完成 (Day={_gameState.GameDay})");
                        return;
                    }
                    _logInfo("[AgentEngine] 游戏尚未就绪，等待...");
                }
                catch (Exception ex) { _logInfo($"[AgentEngine] 冷启动检测失败: {ex.Message}"); }
            }

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

            // 定期状态检测（每 120 tick ≈ 2s，仅 Agent 空闲时）
            if (!AgentOrchestrator.IsRunning && currentTick - _lastStatusCheckTick >= 120)
            {
                _lastStatusCheckTick = currentTick;

                // 暂停过久提醒
                if (_gameState.IsPaused)
                {
                    int nowMs = Environment.TickCount;
                    if (_pauseStartMs == 0) _pauseStartMs = nowMs;
                    int elapsed = unchecked(nowMs - _pauseStartMs);
                    if (_lastPauseRemindMs == 0 && elapsed >= 30000)
                    {
                        _lastPauseRemindMs = nowMs;
                        UIMessageBus.PushUiMessage(UiMessage.System($"游戏已暂停 {elapsed / 1000} 秒，请检查是否需要继续。"));
                    }
                    else if (_lastPauseRemindMs > 0 && unchecked(nowMs - _lastPauseRemindMs) >= 60000)
                    {
                        _lastPauseRemindMs = nowMs;
                        UIMessageBus.PushUiMessage(UiMessage.System($"游戏仍在暂停中 (共 {elapsed / 1000} 秒)。"));
                    }
                }
                else { _pauseStartMs = 0; _lastPauseRemindMs = 0; }
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
            if (!AgentOrchestrator.IsRunning && _gameState.ShouldMorningReport())
            {
                AgentOrchestrator.EnterPlanPhase();
                if (AgentOrchestrator.PaceController == null)
                    AgentOrchestrator.PaceController = new GamePaceController();
                await AgentOrchestrator.PaceController.PauseForPlanning(_mcp, GamePaceController.PlanSpeed);
                _gameState.MarkMorningReportSent();
                await RunAgent(isPlan: true);
                return;
            }

            // 优先级 4: 定期 ACT 检查
            if (!AgentOrchestrator.IsRunning
                && _gameState.ShouldWake(AgentConfigs.Default.IntervalGameHours))
            {
                await RunAgent(isPlan: false);
                return;
            }
        }

        private async Task RunAgent(bool isPlan = false, bool isInterrupted = false)
        {
            GamePaceController.ShouldSkipResume = null;
            AgentOrchestrator.BeginSession();
            _logInfo($"[AgentEngine] 唤醒 commander (Day={_gameState.GameDay}, Plan={isPlan}, Interrupted={isInterrupted})");

            var prompt = await _ctx!.BuildAsync(isInterrupted: isInterrupted);
            // 中断后立即继续：abort 确认后不经过 2s poll，直接构建新 prompt 发送
            AgentLoop.OnContinueAfterInterrupt = async () =>
            {
                AgentOrchestrator.InterruptRequested = false;
                await RunAgent(isInterrupted: true);
            };
            await AgentLoop.RunSessionAsync(prompt, _mcp!, _ccbWs);
            AgentLoop.OnContinueAfterInterrupt = null;

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
