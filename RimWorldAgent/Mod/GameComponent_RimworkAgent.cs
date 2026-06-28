using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimWorldAgent.Core;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Data;
using Verse;

namespace RimWorldAgent
{
    public class GameComponent_RimWorldAgent : GameComponent
    {
        private AgentEngine? _engine;
        private ScribeDbStore? _dbStore;
        private bool _initialized;
        private bool _initializing;
        private int _lastTick;
        private bool _agentTickRunning;

        public GameComponent_RimWorldAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            if (_initializing)
            {
                SafeLog.Warning("[agent-mod] Agent Runtime 正在初始化，跳过重复触发");
                return;
            }

            _initializing = true;
            try
            {
                ShutdownEngine();

                var settings = RimWorldAgentMod.Instance?.Settings;
                if (settings != null && !settings.AgentAutoRun)
                {
                    CoreLog.Info("[agent-mod] AgentAutoRun=false，跳过初始化");
                    return;
                }

                ToolDispatcher.ResetTaskCount();

                var modRoot = Path.GetDirectoryName(
                    typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
                CoreLog.Info($"[agent-mod] DLL 路径 = {modRoot}");
                var defaultProjectPath = Path.Combine(modRoot, "claude-sessions", "rimworld-agent");
                var projectPath = !string.IsNullOrEmpty(settings?.ProjectPath)
                    ? Path.Combine(modRoot, settings!.ProjectPath)
                    : defaultProjectPath;

                var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                    ? Path.Combine(modRoot, settings!.SkillsDir)
                    : Path.GetFullPath(Path.Combine(modRoot, "Skills"));
                var userSkillsDir = Path.GetFullPath(Path.Combine(modRoot, "Skills.d"));

                var asmDir = Path.GetDirectoryName(
                    typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
                var ccbDir = Path.GetFullPath(Path.Combine(asmDir, "cc-companion"));

                var gameHost = settings?.GameMcpHost ?? "localhost";
                var gamePort = settings?.GameMcpPort ?? 9877;
                var customMcpServers = settings?.CustomMcpServers?
                    .Where(s => s != null)
                    .Select(s => new CustomMcpServerConfig
                    {
                        Enabled = s.Enabled,
                        Name = s.Name ?? "",
                        Type = s.Type ?? "http",
                        Url = s.Url ?? "",
                        Command = s.Command ?? "npx",
                        ArgsText = s.ArgsText ?? "",
                        EnvText = s.EnvText ?? "",
                        Timeout = s.Timeout
                    })
                    .ToList() ?? new System.Collections.Generic.List<CustomMcpServerConfig>();
                var dbStore = new ScribeDbStore();
                var gameState = new DirectGameStateProvider();

                var cfg = new AgentEngineConfig
                {
                    ProjectPath = projectPath,
                    SkillsDir = skillsDir,
                    UserSkillsDir = userSkillsDir,
                    McpUrl = $"http://{gameHost}:{gamePort}",
                    McpPort = gamePort,
                    AgentMcpPort = settings?.AgentMcpPort ?? 9878,
                    CustomMcpServers = customMcpServers,
                    CcbPort = 19998,
                    CcbWsUrl = "ws://127.0.0.1:19998",
                    ModelName = settings?.ModelName,
                    CcbAutoStart = true,
                    CcbAutoInstall = settings?.CcbAutoInstall ?? true,
                    CcbDir = ccbDir,
                    // PlanSpeed 已移除
                    TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                    ThinkingMode = settings?.ThinkingMode ?? "adaptive",
                    ThinkingEffort = settings?.ThinkingEffort ?? "high",
                    LogSdkMessages = settings?.LogSdkMessages ?? false,
                    ApiKey = settings?.ApiKey,
                    ApiUrl = settings?.ApiUrl,
                    DiffEnabled = settings?.DiffEnabled ?? true,
                    DiffThreshold = settings?.DiffThreshold ?? 0.30,
                    ClearToolResultSnapshotsOnStart = true,
                };

                var snapshotStore = new SqliteToolResultSnapshotStore(
                    Path.Combine(projectPath, "conversation.db"));
                var engine = new AgentEngine(cfg, dbStore, gameState,
                    toolResultSnapshotStore: snapshotStore,
                    logInfo: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logError: msg => SafeLog.Error($"[agent-core] {msg}"),
                    logDebug: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logWarn: msg => SafeLog.Warning($"[agent-core] {msg}"));
                _engine = engine;

                // 先启动 UIMessageBus，确保 InitAsync 触发 Token 推送时 WS 已就绪
                if (settings?.BridgeHost != "disabled")
                {
                    var bridgeHost = settings?.BridgeHost ?? "0.0.0.0";
                    var bridgePort = settings?.BridgePort ?? 19999;
                    UIMessageBus.Start(bridgeHost, bridgePort);
                }

                await engine.InitAsync();
                if (!ReferenceEquals(_engine, engine))
                {
                    SafeLog.Warning("[agent-mod] 初始化期间 AgentEngine 已被替换或关闭，停止本次初始化");
                    return;
                }
                CoreLog.Info($"[agent-mod] 内部工具已注册 ({InternalToolRegistry.Instance.All.Count}): {string.Join(", ", InternalToolRegistry.Instance.All.Select(t => t.Name))}");

                // conversation store 由 OnSessionIdChanged 在 SDK system.init 到达时创建
                _dbStore = dbStore;

                if (engine.CcbWs != null)
                {
                    if (settings?.LogCcbWsMessages == true)
                        CcbWebSocket.WsLogFilePath = Path.Combine(projectPath!, "ccb-ws-log.txt");
                    AgentLoop.WireUIMessageBus(engine.CcbWs);
                }
                else
                {
                    SafeLog.Warning("[agent-mod] CcbWs 为 null，UI 总线未启动");
                }

                _lastTick = 0;
                _initialized = true;
                SafeLog.Info("[agent-mod] Agent Runtime 初始化完成");
            }
            catch (Exception ex)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[agent-mod] InitAgentRuntime 异常:");
                for (var e = ex; e != null; e = e.InnerException)
                    sb.AppendLine($"  [{e.GetType().Name}] {e.Message}\n{e.StackTrace}");
                SafeLog.Error(sb.ToString());
                _initialized = false;
            }
            finally
            {
                _initializing = false;
            }
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            SafeLog.Flush();
            if (!_initialized || _engine == null) return;

            _engine.Tick();
            UIMessageBus.IsReady = _engine.CcbWs?.IsReady ?? false;

            if (Find.CurrentMap == null) return;

            _lastTick++;
            if (_lastTick < 125) return; // ~2000ms @60fps
            _lastTick = 0;
            if (_agentTickRunning) return;
            _ = AgentTickAsync();
        }

        private async Task AgentTickAsync()
        {
            if (_engine == null) return;
            _agentTickRunning = true;
            try
            {
                await _engine.TickAsync();
            }
            catch (Exception ex)
            {
                SafeLog.Warning($"[agent-mod] Agent Tick 异常: {FormatExceptionChain(ex)}");
            }
            finally
            {
                _agentTickRunning = false;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            _dbStore?.ScribeExpose();
        }

        public void ShutdownEngine()
        {
            try
            {
                CoreLog.Info("[agent-mod] 返回主菜单，开始关闭 Agent 和 CCB...");
                try { UIMessageBus.Stop(); }
                catch (Exception ex) { SafeLog.Warning($"[agent-mod] UIMessageBus.Stop 异常 (可忽略): {ex.GetType().Name}: {ex.Message}"); }
                try
                {
                    _engine?.Dispose();
                    _engine = null;
                    _initialized = false;
                    CoreLog.Info("[agent-mod] Agent 和 CCB 已关闭");
                }
                catch (Exception ex) { CoreLog.Error($"[agent-mod] 关闭 Agent 失败: {ex.Message}"); }
                try { CcbManager.KillStaleProcesses(); }
                catch (Exception ex) { SafeLog.Warning($"[agent-mod] KillStaleProcesses 异常: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                SafeLog.Warning($"[agent-mod] ShutdownEngine 异常 (非致命): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }

    }
}
