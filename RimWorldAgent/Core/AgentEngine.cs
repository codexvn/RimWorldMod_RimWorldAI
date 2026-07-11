using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentTransport;
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
        public string? UserSkillsDir { get; set; }
        public string PromptPath { get; set; } = "";
        public string? SkillsDescPath { get; set; }
        public string McpUrl { get; set; } = "http://localhost:9877";
        public int AgentMcpPort { get; set; } = 9878;
        public string AcpNodePath { get; set; } = "node";
        public string NodeHostDir { get; set; } = "";
        public string NodeHostEntryPoint { get; set; } = "dist/main.js";
        public int IpcRequestTimeoutSeconds { get; set; } = 300;
        public AcpAgentServerDefinition? AcpBackend { get; set; }
        public bool AcpAutoStart { get; set; } = true;
        // PlanSpeed 已移除 — Plan/Act 阶段均强制暂停，仅 Advance 可推进
        public long TokenBudgetLimit { get; set; }
        public bool DiffEnabled { get; set; } = true;
        public double DiffThreshold { get; set; } = 0.30;
        public bool ClearToolResultSnapshotsOnStart { get; set; }
    }

    /// <summary>Agent 引擎 — ACP session + UI bus + MCP + 调度循环。EXE/MOD 共享。</summary>
    internal sealed class AcpAgentLaunch
    {
        public string Name { get; }
        public string Command { get; }
        public IReadOnlyList<string> Args { get; }
        public string WorkingDirectory { get; }
        public IReadOnlyDictionary<string, string> Env { get; }

        public AcpAgentLaunch(string name, string command, IReadOnlyList<string> args,
            string workingDirectory, IReadOnlyDictionary<string, string> env)
        {
            Name = name;
            Command = command;
            Args = args;
            WorkingDirectory = workingDirectory;
            Env = env;
        }
    }

    public class AgentEngine : IDisposable
    {
        private readonly AgentEngineConfig _cfg;
        private readonly IDbStore _dbStore;
        private readonly IGameStateProvider _gameState;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly Action<string> _logDebug;
        private readonly Action<string> _logWarn;
        private readonly IToolResultSnapshotStore _toolResultSnapshotStore;
        private IAgentSession? _agentSession;
        private McpClient? _mcp;
        private ContextBuilder? _ctx;
        private string _gameSessionId = "";
        private bool _initialized;
        private int _lastStatusCheckTick;
        private int _pauseStartMs;
        private int _lastPauseRemindMs;
        private SimpleMspServer.McpServiceHost? _agentHost;
        private bool _initializing;
        private bool _disposed;
        private CancellationTokenSource? _enforceCts;
        private Task? _enforceTask;

        public IAgentSession? AgentSession => _agentSession;
        public McpClient? McpClient => _mcp;
        public bool IsReady => _initialized && _mcp != null && _agentSession?.IsReady == true;

        public void SetGameSessionId(string sessionId)
            => _gameSessionId = ExtractSessionId(sessionId);

        public static string ExtractGameSessionId(string rawSessionId)
            => ExtractSessionId(rawSessionId);

        public AgentEngine(AgentEngineConfig cfg, IDbStore dbStore, IGameStateProvider gameState,
            Action<string>? logInfo = null, Action<string>? logError = null, Action<string>? logDebug = null,
            Action<string>? logWarn = null, IToolResultSnapshotStore? toolResultSnapshotStore = null)
        {
            _cfg = cfg;
            _dbStore = dbStore;
            _gameState = gameState;
            _logInfo = logInfo ?? (msg => { });
            _logError = logError ?? (msg => { });
            _logDebug = logDebug ?? (msg => { });
            _logWarn = logWarn ?? (msg => { });
            _toolResultSnapshotStore = toolResultSnapshotStore ?? new MemoryToolResultSnapshotStore();
        }

        /// <summary>完整启动流程：Skills → AgentMCP → Node ACP Host/IPC session → MCP → SSE</summary>
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
            var skillsDescPath = string.IsNullOrWhiteSpace(_cfg.SkillsDescPath)
                ? Path.Combine(_cfg.ProjectPath, "skills-desc.txt")
                : _cfg.SkillsDescPath;
            InternalToolRegistry.SkillsDescPath = skillsDescPath;
            InternalToolRegistry.UpdateSkillsDesc();

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
                    _logDebug($"[AgentEngine] MCP 就绪检查失败: {ex.GetType().Name}: {FormatExceptionChain(ex)}");
                    if (ex.InnerException != null) _logDebug($"[AgentEngine] MCP InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    _logInfo("[AgentEngine] 游戏 MCP 尚未就绪，3s 后重试...");
                    await Task.Delay(3000);
                }
            }
            if (_disposed) return false;

            // 游戏事件订阅 + 游戏工具代理 → Agent MCP（必须在 ACP session 之前完成）
            AgentLoop.WireEvents(mcp);
            _logInfo("[AgentEngine] 开始代理游戏工具...");

            // 黑名单：不允许 LLM 调用的游戏工具
            // 右键菜单类（殖民者自己会做，AI 调用反而打断工作流）
            ProxyToolProvider.ToolBlacklist.Add("get_right_click_menu");
            ProxyToolProvider.ToolBlacklist.Add("select_right_click");
            // 游戏速度（由系统自动管理，AI 不得手动切换）
            ProxyToolProvider.ToolBlacklist.Add("toggle_pause");

            if (_cfg.ClearToolResultSnapshotsOnStart)
                _toolResultSnapshotStore.Clear();

            var resultPipeline = new ToolResultPipeline(new IToolResultProcessor[]
            {
                new DiffProcessor(
                    _toolResultSnapshotStore,
                    new ToolResultDiffEngine(),
                    _cfg.DiffEnabled,
                    _cfg.DiffThreshold),
                new SuffixProcessor()
            });

            var proxy = new ProxyToolProvider(mcp, resultPipeline, () => _gameSessionId);
            await proxy.RefreshToolsAsync();
            if (_disposed) return false;
            _agentHost.RegisterProvider(proxy);
            _logInfo("[AgentEngine] 游戏工具代理已注册");

            // Agent session_id 变更时同步到 MCP（Scribe 持久化）
            AgentLoop.OnSessionIdChanged += sid =>
            {
                _gameSessionId = sid;
                _logInfo($"[AgentEngine] Agent sessionId 到达: {sid}");

                // MCP (Scribe)
                try
                {
                    _ = mcp.CallTool("set_session_id", new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(sid) });
                    _logInfo($"[AgentEngine] MCP set_session_id: {sid}");
                }
                catch (Exception ex) { _logWarn($"[AgentEngine] MCP set_session_id 失败: {FormatExceptionChain(ex)}"); }

                // conversation store (sessionId)
                var dbPath = Path.Combine(_cfg.ProjectPath, "conversation.db");
                try
                {
                    (AgentLoop.ConversationStore as IDisposable)?.Dispose();
                    AgentLoop.ConversationStore = new SqliteConversationStore(dbPath, sid);
                    _logInfo($"[AgentEngine] SqliteConversationStore 已就绪 (save_id={sid})");
                }
                catch (Exception ex) { _logWarn($"[AgentEngine] 创建 SqliteConversationStore 失败: {FormatExceptionChain(ex)}"); }
            };

            // 从 MCP Scribe 取出存档持久化的 Agent sessionId；ACP 可直接 resume/load
            var sidFile = Path.Combine(_cfg.ProjectPath, "session-id.txt");
            try { if (File.Exists(sidFile)) { File.Delete(sidFile); _logInfo("[AgentEngine] 已删除旧的 session-id.txt"); } }
            catch (Exception ex) { _logWarn($"[AgentEngine] 删除旧 session-id.txt 失败: {FormatExceptionChain(ex)}"); }

            try
            {
                var (success, sid) = await mcp.TryCallTool("get_session_id");
                var sessionId = ExtractSessionId(sid);
                if (success && !string.IsNullOrEmpty(sessionId))
                {
                    _gameSessionId = sessionId;
                    File.WriteAllText(sidFile, sessionId);
                    _logInfo($"[AgentEngine] session-id.txt 已写入: {_gameSessionId}");
                }
                else
                {
                    _logInfo($"[AgentEngine] sessionId 尚未就绪（MCP 返回失败或为空），跳过 session-id.txt");
                }
            }
            catch (Exception ex) { _logWarn($"[AgentEngine] get_session_id 异常: {FormatExceptionChain(ex)}"); }

            // Node ACP Host：C# 只传递 IPC DTO，ACP 由 Node Host 负责
            var acpReady = false;
            var launch = ResolveAcpLaunch();
            var nodeHostEntryPoint = ResolveNodeHostEntryPoint();
            if (_cfg.AcpAutoStart && launch != null && nodeHostEntryPoint != null)
            {
                try
                {
                    _logInfo($"[AgentEngine] Node ACP Host + backend={launch.Name}, command={launch.Command}, 开始启动...");
                    _agentSession = new NodeAgentSession(_cfg, launch, nodeHostEntryPoint, _logInfo, _logWarn, _logError);
                    AgentOrchestrator.CancelCurrentSession = () => _agentSession.CancelAsync(CancellationToken.None);
                    await _agentSession.InitializeAsync(CancellationToken.None);
                    if (!string.IsNullOrEmpty(_gameSessionId) && _agentSession.CanLoadSession)
                        await _agentSession.LoadAsync(_gameSessionId, CancellationToken.None);
                    else if (!string.IsNullOrEmpty(_gameSessionId) && _agentSession.CanResumeSession)
                        await _agentSession.ResumeAsync(_gameSessionId, CancellationToken.None);
                    else
                    {
                        if (!string.IsNullOrEmpty(_gameSessionId))
                            _logWarn("[AgentEngine] Backend 不支持 load/resume，创建新 ACP session。");
                        await _agentSession.NewAsync(CancellationToken.None);
                    }
                    AgentLoop.WireUIMessageBus(_agentSession);
                    _logInfo("[AgentEngine] Node ACP session: 已连接");
                    acpReady = true;
                }
                catch (Exception ex)
                {
                    _logError("[AgentEngine] Node ACP Host 启动失败: " + FormatExceptionChain(ex));
                    _agentSession?.Dispose();
                    _agentSession = null;
                }
            }
            else
            {
                _logError("[AgentEngine] Node ACP Host 或 backend 配置不可用。");
            }

            if (!acpReady) _logInfo("[AgentEngine] ACP: 未就绪 (Agent 不可用)");

            // 启动暂停兜底守护线程
            StartEnforceLoop();

            // PlanSpeed 已移除
            _ctx = new ContextBuilder(mcp);

            _initialized = true;
            return acpReady;
            }
            finally
            {
                _initializing = false;
            }
        }

        /// <summary>同步维护。ACP 使用 stdio 长连接，当前不做端口重连。</summary>
        public void Tick()
        {
            if (!_initialized) return;
        }

        public async Task TickAsync()
        {
            if (_mcp == null || _ctx == null || _agentSession == null || !_agentSession.IsReady) return;

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
                        await AgentOrchestrator.PaceController.PauseForPlanning(_mcp);
                        await RunAgent(isPlan: true);
                        _logInfo($"[AgentEngine] 冷启动完成 (Day={_gameState.GameDay})");
                        return;
                    }
                    _logInfo("[AgentEngine] 游戏尚未就绪，等待...");
                }
                catch (Exception ex) { _logInfo($"[AgentEngine] 冷启动检测失败: {FormatExceptionChain(ex)}"); }
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

            // 优先级 1: 中断请求 + Agent 运行中 → 等待 AgentLoop 中 Cancel 处理
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
                await AgentOrchestrator.PaceController.PauseForPlanning(_mcp);
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
            var session = _agentSession;
            if (session == null || !session.IsReady)
            {
                _logWarn("[AgentEngine] ACP session 未就绪，跳过本轮唤醒");
                return;
            }

            AgentOrchestrator.BeginSession();
            try
            {
                _logInfo($"[AgentEngine] 唤醒 commander (Day={_gameState.GameDay}, Plan={isPlan}, Interrupted={isInterrupted})");

                var prompt = await _ctx!.BuildAsync(isInterrupted: isInterrupted);
                await AgentLoop.RunSessionAsync(prompt, _mcp!, session);
            }
            finally
            {
                AgentOrchestrator.EndSession();
                _logInfo("[AgentEngine] commander 休眠");
            }
        }

        private void StartEnforceLoop()
        {
            _enforceCts?.Cancel();
            _enforceCts = new CancellationTokenSource();
            var token = _enforceCts.Token;
            _enforceTask = Task.Run(async () =>
            {
                // 等待 MCP 就绪（给每次 StartEnforceLoop 留充足等待时间）
                for (int retry = 0; retry < 30 && !token.IsCancellationRequested; retry++)
                {
                    if (_mcp != null)
                    {
                        try
                        {
                            await _mcp.CallTool("get_game_speed");
                            break; // 成功即退出等待
                        }
                        catch (Exception ex)
                        {
                            _logDebug($"[AgentEngine] 暂停兜底等待 MCP 失败: {FormatExceptionChain(ex)}");
                        }
                    }
                    try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
                }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, token);
                        if (token.IsCancellationRequested) break;

                        if (_mcp == null) continue;
                        if (AgentOrchestrator.IsAdvancing) continue;

                        var speedResult = await _mcp.CallTool("get_game_speed");
                        if (speedResult != null)
                        {
                            bool isPaused = false;
                            try
                            {
                                var speedDoc = JsonDocument.Parse(speedResult);
                                if (speedDoc.RootElement.TryGetProperty("paused", out var pausedEl))
                                    isPaused = pausedEl.GetBoolean();
                            }
                            catch (Exception parseEx) { _logDebug($"[AgentEngine] 暂停兜底: 解析 game_speed 结果失败: {FormatExceptionChain(parseEx)}"); isPaused = false; }

                            if (!isPaused)
                            {
                                _logInfo("[AgentEngine] 兜底暂停: 检测到非推进期游戏未暂停，强制暂停");
                                await _mcp.CallTool("toggle_pause", new Dictionary<string, JsonElement>
                                {
                                    ["speed"] = JsonSerializer.SerializeToElement("paused")
                                });
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logWarn($"[AgentEngine] 暂停兜底异常: {FormatExceptionChain(ex)}");
                    }
                }
            }, token);
            _logInfo("[AgentEngine] 暂停兜底守护已启动");
        }

        public void Dispose()
        {
            _disposed = true;
            _initialized = false;
            try { _enforceCts?.Cancel(); } catch (Exception ex) { _logWarn($"[AgentEngine] 取消暂停兜底失败: {FormatExceptionChain(ex)}"); }
            AgentOrchestrator.CancelCurrentSession = null;
            _agentSession?.Dispose();
            _agentSession = null;
            _mcp?.Dispose();
            _mcp = null;
            _agentHost?.Stop();
            _agentHost = null;
            (_toolResultSnapshotStore as IDisposable)?.Dispose();
        }


        private AcpAgentLaunch? ResolveAcpLaunch()
        {
            if (_cfg.AcpBackend != null)
            {
                var configured = _cfg.AcpBackend;
                if (!string.IsNullOrWhiteSpace(configured.Command))
                {
                    var workingDirectory = string.IsNullOrWhiteSpace(configured.WorkingDirectory)
                        ? _cfg.ProjectPath
                        : configured.WorkingDirectory!;
                    if (!Path.IsPathRooted(workingDirectory))
                        workingDirectory = Path.GetFullPath(Path.Combine(_cfg.ProjectPath, workingDirectory));
                    var command = configured.Command;
                    if (!Path.IsPathRooted(command)
                        && (command.IndexOf(Path.DirectorySeparatorChar) >= 0
                            || command.IndexOf(Path.AltDirectorySeparatorChar) >= 0))
                    {
                        command = Path.GetFullPath(Path.Combine(workingDirectory, command));
                    }
                    return new AcpAgentLaunch(configured.Name, command, configured.Args,
                        workingDirectory, configured.Env);
                }

                _logWarn("[AgentEngine] ACP Backend 缺少启动命令: " + configured.Name);
                return null;
            }

            _logWarn("[AgentEngine] 未配置 ACP Backend。");
            return null;
        }

        private string? ResolveNodeHostEntryPoint()
        {
            var hostDir = _cfg.NodeHostDir;
            if (string.IsNullOrWhiteSpace(hostDir))
                hostDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rimworld-acp-host");
            if (!Directory.Exists(hostDir))
                hostDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "RimWorldAgent", "Node", "rimworld-acp-host"));
            if (!Directory.Exists(hostDir)) return null;

            var entryPoint = string.IsNullOrWhiteSpace(_cfg.NodeHostEntryPoint) ? "dist/main.js" : _cfg.NodeHostEntryPoint;
            var path = Path.Combine(hostDir, entryPoint.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : null;
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }

        private static string ExtractSessionId(string rawSessionId)
        {
            var sessionId = (rawSessionId ?? "").Split('\n')[0].Trim();
            if (sessionId.Length == 0 || sessionId.Length > 512) return "";
            foreach (var character in sessionId)
            {
                if (char.IsControl(character) || char.IsWhiteSpace(character)) return "";
            }
            return sessionId;
        }
    }
}
