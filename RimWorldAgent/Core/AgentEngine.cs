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
    public class CustomMcpServerConfig
    {
        public bool Enabled { get; set; } = true;
        public string Name { get; set; } = "";
        public string Type { get; set; } = "http";
        public string Url { get; set; } = "";
        public string Command { get; set; } = "npx";
        public string ArgsText { get; set; } = "";
        public string EnvText { get; set; } = "";
        public int Timeout { get; set; } = 300000;
    }

    /// <summary>Agent 引擎配置 — 构造后传 InitAsync</summary>
    public class AgentEngineConfig
    {
        public string ProjectPath { get; set; } = "";
        public string? SkillsDir { get; set; }
        public string? UserSkillsDir { get; set; }
        public string McpUrl { get; set; } = "http://localhost:9877";
        public int McpPort { get; set; } = 9877;
        public int AgentMcpPort { get; set; } = 9878;
        public List<CustomMcpServerConfig> CustomMcpServers { get; set; } = new List<CustomMcpServerConfig>();
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
        public string ThinkingMode { get; set; } = "adaptive";
        public string ThinkingEffort { get; set; } = "high";
        public bool LogSdkMessages { get; set; }
        public string? ApiKey { get; set; }
        public string? ApiUrl { get; set; }
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
        private int _lastStatusCheckTick;
        private int _pauseStartMs;
        private int _lastPauseRemindMs;
        private SimpleMspServer.McpServiceHost? _agentHost;
        private bool _initializing;
        private bool _disposed;

        public CcbWebSocket? CcbWs => _ccbWs;
        public McpClient? McpClient => _mcp;
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
            if (_disposed) return false;
            if (_initializing)
            {
                _logWarn("[AgentEngine] InitAsync 已在运行，跳过重复初始化");
                return false;
            }

            _initializing = true;
            try
            {

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
            var userSkillsDir = _cfg.UserSkillsDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills.d");
            InternalToolRegistry.Instance.LoadSkills(skillsDir, userSkillsDir);

            // Agent MCP Server
            _agentHost = new SimpleMspServer.McpServiceHost(_cfg.AgentMcpPort,
                log: new SimpleMspServer.DelegateMspLog(_logInfo));
            _agentHost.RegisterProvider(InternalToolRegistry.Instance);
            _agentHost.Start();
            _logInfo($"[AgentEngine] AgentMCP :{_cfg.AgentMcpPort}");

            // MCP 客户端 — 连接游戏 MCP Server，等待就绪后再代理工具
            _mcp = new McpClient(_cfg.McpUrl);

            _logInfo("[AgentEngine] 等待游戏 MCP 服务就绪...");
            var mcp = _mcp;
            if (mcp == null)
                throw new InvalidOperationException("McpClient 创建失败。");

            while (!_disposed)
            {
                try
                {
                    var tools = await mcp.ListToolsAsync();
                    if (_disposed) return false;
                    _logInfo($"[AgentEngine] 游戏 MCP 已连接 ({tools.Count} 工具)");
                    break;
                }
                catch (Exception ex)
                {
                    if (_disposed) return false;
                    _logDebug($"[AgentEngine] MCP 就绪检查失败: {ex.GetType().Name}: {ex.Message}");
                    if (ex.InnerException != null) _logDebug($"[AgentEngine] MCP InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    _logInfo("[AgentEngine] 游戏 MCP 尚未就绪，3s 后重试...");
                    await Task.Delay(3000);
                }
            }
            if (_disposed) return false;

            // 游戏事件订阅 + 游戏工具代理 → Agent MCP（必须在 CCB 之前完成）
            AgentLoop.WireEvents(mcp);
            _logInfo("[AgentEngine] 开始代理游戏工具...");
            var proxy = new ProxyToolProvider(mcp);
            await proxy.RefreshToolsAsync();
            if (_disposed) return false;
            _agentHost.RegisterProvider(proxy);
            _logInfo("[AgentEngine] 游戏工具代理已注册");

            // SDK session_id 变更时同步到 MCP（Scribe 持久化）
            AgentLoop.OnSessionIdChanged += sid =>
            {
                try
                {
                    _ = mcp.CallTool("set_session_id", new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(sid) });
                    _logInfo($"[AgentEngine] MCP set_session_id: {sid}");
                }
                catch (Exception ex) { _logWarn($"[AgentEngine] MCP set_session_id 失败: {ex.Message}"); }
            };

            // 从 MCP 获取存档 sessionId → 写 session-id.txt → 供 companion 启动恢复
            // MCP 连接成功时 LoadedGame/StartedNewGame 已完成，sessionId 立即可用
            try
            {
                var rawId = await mcp.CallTool("get_session_id");
                var sid = rawId?.Trim();
                if (!string.IsNullOrEmpty(sid))
                {
                    var sidFile = Path.Combine(_cfg.ProjectPath, "session-id.txt");
                    File.WriteAllText(sidFile, sid);
                    _logInfo($"[AgentEngine] session-id.txt 已写入: {sid}");
                }
            }
            catch (Exception ex) { _logWarn($"[AgentEngine] get_session_id 失败: {ex.Message}"); }

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
                    budgetLimit: _cfg.TokenBudgetLimit, budgetAction: "Block",
                    logSdk: _cfg.LogSdkMessages,
                    customMcpServers: _cfg.CustomMcpServers,
                    apiKey: _cfg.ApiKey, apiUrl: _cfg.ApiUrl);
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
                        ThinkingEffort = _cfg.ThinkingEffort
                    };
                    var wsOk = await _ccbWs.ConnectAsync();
                    _logInfo($"[AgentEngine] WS ConnectAsync = {wsOk}");
                    if (wsOk)
                    {
                        AgentOrchestrator.CcbWs = _ccbWs;
                        AgentLoop.WireUIMessageBus(_ccbWs);
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
            _ctx = new ContextBuilder(mcp);

            _initialized = true;
            return ccbReady;
            }
            finally
            {
                _initializing = false;
            }
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
                        ThinkingEffort = _cfg.ThinkingEffort
                    };
                    _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                    {
                        if (t.Result)
                        {
                            AgentOrchestrator.CcbWs = _ccbWs!;
                            AgentLoop.WireUIMessageBus(_ccbWs!);
                        }
                        else { _ccbWs?.Dispose(); _ccbWs = null; }
                    });
                }
            }

            // WS 被动断开自动重连（CcbWebSocket 内部 ScheduleReconnect 处理）
            if (_ccbWs != null && _ccbWs.State == CcbClientState.Disconnected)
            {
                _ = _ccbWs.ConnectAsync().ContinueWith(t =>
                {
                    if (t.Result)
                    {
                        AgentOrchestrator.CcbWs = _ccbWs!;
                        AgentLoop.WireUIMessageBus(_ccbWs!);
                    }
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
                        // 新游戏默认进入 PLAN + 暂停，让 AI 先了解情况再行动
                        AgentOrchestrator.EnterPlanPhase();
                        if (AgentOrchestrator.PaceController == null)
                            AgentOrchestrator.PaceController = new GamePaceController();
                        await AgentOrchestrator.PaceController.PauseForPlanning(_mcp, GamePaceController.PlanSpeed);
                        await RunAgent(isPlan: true);
                        _logInfo($"[AgentEngine] 冷启动完成 (Day={_gameState.GameDay})");
                        return;
                    }
                    _logInfo("[AgentEngine] 游戏尚未就绪，等待...");
                }
                catch (Exception ex) { _logInfo($"[AgentEngine] 冷启动检测失败: {ex.Message}"); }
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
            var ccbWs = _ccbWs;
            if (ccbWs == null || !ccbWs.IsReady)
            {
                _logWarn("[AgentEngine] CCB WS 未就绪，跳过本轮唤醒");
                return;
            }

            GamePaceController.ShouldSkipResume = null;
            AgentOrchestrator.BeginSession();
            try
            {
                _logInfo($"[AgentEngine] 唤醒 commander (Day={_gameState.GameDay}, Plan={isPlan}, Interrupted={isInterrupted})");

                var prompt = await _ctx!.BuildAsync(isInterrupted: isInterrupted);
                await AgentLoop.RunSessionAsync(prompt, _mcp!, ccbWs);
            }
            finally
            {
                AgentOrchestrator.EndSession();
                _logInfo("[AgentEngine] commander 休眠");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _initialized = false;
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
