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
        private IConversationStore? _convStore;
        private bool _initialized;
        private int _lastTick;

        public GameComponent_RimWorldAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            try
            {
                ShutdownEngine();

                var settings = RimWorldAgentMod.Instance?.Settings;
                if (settings != null && !settings.AgentAutoRun)
                {
                    CoreLog.Info("[agent-mod] AgentAutoRun=false，跳过初始化");
                    return;
                }
                _initialized = true;

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
                    PlanSpeed = settings?.PlanSpeed ?? "paused",
                    TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                    ThinkingMode = settings?.ThinkingMode ?? "adaptive",
                    ThinkingEffort = settings?.ThinkingEffort ?? "high",
                    LogSdkMessages = settings?.LogSdkMessages ?? false,
                };

                _engine = new AgentEngine(cfg, dbStore, gameState,
                    logInfo: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logError: msg => SafeLog.Error($"[agent-core] {msg}"),
                    logDebug: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logWarn: msg => SafeLog.Warning($"[agent-core] {msg}"));

                // 先启动 UIMessageBus，确保 InitAsync 触发 Token 推送时 WS 已就绪
                if (settings?.BridgeHost != "disabled")
                {
                    var bridgeHost = settings?.BridgeHost ?? "0.0.0.0";
                    var bridgePort = settings?.BridgePort ?? 19999;
                    UIMessageBus.Start(bridgeHost, bridgePort);
                }

                await _engine.InitAsync();
                CoreLog.Info($"[agent-mod] 内部工具已注册 ({InternalToolRegistry.Instance.All.Count}): {string.Join(", ", InternalToolRegistry.Instance.All.Select(t => t.Name))}");

                // 通过 MCP 获取存档 sessionId，创建 SQLite 持久化存储
                var mcp = _engine.McpClient;
                if (mcp == null)
                    throw new InvalidOperationException("McpClient 不可用，无法获取会话 ID");

                var rawId = await mcp.CallTool("get_session_id");
                var sessionId = rawId?.Split('\n')[0]?.Trim();  // 首行=纯GUID，后续为ToolRegistry自动追加的[游戏速度]
                if (string.IsNullOrEmpty(sessionId))
                    throw new InvalidOperationException("get_session_id 返回空，当前可能未加载存档");

                // 先设置原生 DLL 搜索路径，再初始化 SQLite
                NativeResolver.Setup(Path.GetDirectoryName(modRoot)!);

                var dbPath = Path.Combine(projectPath, "conversation.db");
                _convStore = new SqliteConversationStore(dbPath, sessionId!);
                AgentLoop.ConversationStore = _convStore;
                CoreLog.Info($"[agent-mod] SqliteConversationStore 已就绪 (save_id={sessionId})");

                _dbStore = dbStore;

                if (_engine.CcbWs != null)
                {
                    if (settings?.LogCcbWsMessages == true)
                        CcbWebSocket.WsLogFilePath = Path.Combine(projectPath!, "ccb-ws-log.txt");
                    AgentLoop.WireUIMessageBus(_engine.CcbWs);
                }
                else
                {
                    SafeLog.Warning("[agent-mod] CcbWs 为 null，UI 总线未启动");
                }

                _lastTick = 0;
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
            _ = AgentTickAsync();
        }

        private async Task AgentTickAsync()
        {
            if (_engine == null) return;
            await _engine.TickAsync();
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
                (_convStore as IDisposable)?.Dispose();
                _convStore = null;
                AgentLoop.ConversationStore = null;
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

    }
}
