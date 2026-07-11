using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RimWorldAgent.Core;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.AgentTransport;
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
                var defaultProjectPath = AgentRuntimePaths.GetDefaultProjectDirectory(modRoot);
                var projectPath = !string.IsNullOrEmpty(settings?.ProjectPath)
                    ? Path.Combine(modRoot, settings!.ProjectPath)
                    : defaultProjectPath;

                var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                    ? Path.Combine(modRoot, settings!.SkillsDir)
                    : Path.GetFullPath(Path.Combine(modRoot, AgentRuntimePaths.BuiltinSkillsDirectoryName));
                var userSkillsDir = Path.GetFullPath(Path.Combine(modRoot, AgentRuntimePaths.UserSkillsDirectoryName));

                var asmDir = Path.GetDirectoryName(
                    typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
                var nodeHostDir = Path.GetFullPath(Path.Combine(asmDir, AgentRuntimePaths.NodeHostDirectoryName));
                settings?.EnsureAcpBackendDefaults();
                var selectedBackendMatches = settings?.AcpBackends?
                    .Where(backend => backend != null
                        && backend.Enabled
                        && IsAcpBackendIdValid(backend.Id)
                        && backend.Id == settings.SelectedAcpBackendId)
                    .ToList();
                var selectedBackend = selectedBackendMatches?.Count == 1 ? selectedBackendMatches[0] : null;
                if (settings != null && selectedBackend == null)
                    SafeLog.Warning($"[agent-mod] Backend 配置无效或未选择: {settings.SelectedAcpBackendId}");

                var gameHost = settings?.GameMcpHost ?? "localhost";
                var gamePort = settings?.GameMcpPort ?? 9877;
                var nodePath = NodeRuntimeLocator.Resolve(settings?.NodeExecutablePath);
                if (string.IsNullOrWhiteSpace(nodePath))
                    throw new InvalidOperationException("未找到可用的 Node.js 22+ 运行时。它用于启动 ACP Host，请安装 Node.js，或在 RimWorld Agent 设置中指定 Node.js 可执行文件路径（Windows: node.exe；macOS/Linux: node）。");
                if (!NodeRuntimeLocator.IsVersionSupported(nodePath!, 22, out var nodeVersion))
                    throw new InvalidOperationException($"Node.js 版本不受支持: {nodeVersion}。Node ACP Host 需要 Node.js 22 或更高版本。");
                var dbStore = new ScribeDbStore();
                var gameState = new DirectGameStateProvider();

                var cfg = new AgentEngineConfig
                {
                    ProjectPath = projectPath,
                    SkillsDir = skillsDir,
                    UserSkillsDir = userSkillsDir,
                    PromptPath = Path.Combine(asmDir, AgentRuntimePaths.PromptFileName),
                    SkillsDescPath = Path.Combine(projectPath, AgentRuntimePaths.SkillsDescriptionFileName),
                    McpUrl = $"http://{gameHost}:{gamePort}",
                    AgentMcpPort = settings?.AgentMcpPort ?? 9878,
                    AcpNodePath = nodePath!,
                    NodeHostDir = nodeHostDir,
                    NodeHostEntryPoint = AgentRuntimePaths.NodeHostDefaultEntryPoint,
                    LogAcpIpc = settings?.LogAcpIpc ?? false,
                    AcpBackend = BuildAcpBackendDefinition(selectedBackend, nodePath!),
                    AcpAutoStart = true,
                    // PlanSpeed 已移除
                    TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                    DiffEnabled = settings?.DiffEnabled ?? true,
                    DiffThreshold = settings?.DiffThreshold ?? 0.30,
                    ClearToolResultSnapshotsOnStart = true,
                };

                var snapshotStore = new SqliteToolResultSnapshotStore(
                    Path.Combine(projectPath, AgentRuntimePaths.ConversationDatabaseFileName));
                var engine = new AgentEngine(cfg, dbStore, gameState,
                    toolResultSnapshotStore: snapshotStore,
                    logInfo: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logError: msg => SafeLog.Error($"[agent-core] {msg}"),
                    logDebug: msg => SafeLog.Info($"[agent-core] {msg}"),
                    logWarn: msg => SafeLog.Warning($"[agent-core] {msg}"));
                _engine = engine;

                AcpIpcLogger.LogFilePath = (settings?.LogAcpIpc ?? false) ? Path.Combine(projectPath, "acp-ipc-log.txt") : null;

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

                // conversation store 由 OnSessionIdChanged 在 ACP session/update 到达时创建
                _dbStore = dbStore;

                if (engine.AgentSession != null)
                {
                    AgentLoop.WireUIMessageBus(engine.AgentSession);
                }
                else
                {
                    SafeLog.Warning("[agent-mod] ACP session 为 null，UI 总线未启动");
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
            UIMessageBus.IsReady = _engine.AgentSession?.IsReady ?? false;

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
                CoreLog.Info("[agent-mod] 返回主菜单，开始关闭 Agent...");
                try { UIMessageBus.Stop(); }
                catch (Exception ex) { SafeLog.Warning($"[agent-mod] UIMessageBus.Stop 异常 (可忽略): {FormatExceptionChain(ex)}"); }
                try
                {
                    _engine?.Dispose();
                    _engine = null;
                    _initialized = false;
                    CoreLog.Info("[agent-mod] Agent 已关闭");
                }
                catch (Exception ex) { CoreLog.Error($"[agent-mod] 关闭 Agent 失败: {FormatExceptionChain(ex)}"); }
            }
            catch (Exception ex)
            {
                SafeLog.Warning($"[agent-mod] ShutdownEngine 异常 (非致命): {FormatExceptionChain(ex)}\n{ex.StackTrace}");
            }
        }

        internal static AcpAgentServerDefinition? BuildAcpBackendDefinition(AcpBackendSetting? setting, string nodePath)
        {
            if (setting == null) return null;
            var backend = setting;
            var command = backend.Command?.Trim() ?? "";
            if (string.Equals(command, AgentRuntimePaths.NodeCommandName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, AgentRuntimePaths.NodeExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                command = nodePath;
            }
            return new AcpAgentServerDefinition
            {
                Name = string.IsNullOrWhiteSpace(backend.Id) ? "claude-agent-acp" : backend.Id.Trim(),
                Command = command,
                Args = ParseArguments(backend.ArgsText),
                WorkingDirectory = string.IsNullOrWhiteSpace(backend.WorkingDirectory)
                    ? null
                    : backend.WorkingDirectory.Trim(),
                Env = ParseEnvironment(backend.EnvText),
                SessionConfigSelections = (backend.SessionConfigSelections ?? new List<AcpSessionConfigSelection>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ConfigId))
                    .Select(item => new AcpSessionConfigSelectionValue
                    {
                        ConfigId = item.ConfigId.Trim(),
                        Type = string.IsNullOrWhiteSpace(item.Type) ? "select" : item.Type.Trim(),
                        Value = item.Value ?? ""
                    })
                    .ToList()
            };
        }

        private static bool IsAcpBackendIdValid(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            return id!.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.');
        }

        private static List<string> ParseArguments(string? text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var current = new StringBuilder();
            char quote = '\0';
            for (var i = 0; i < text!.Length; i++)
            {
                var ch = text[i];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                    else if (ch == '\\' && i + 1 < text.Length && text[i + 1] == quote) current.Append(text[++i]);
                    else current.Append(ch);
                    continue;
                }

                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    if (current.Length == 0) continue;
                    result.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(ch);
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        private static Dictionary<string, string> ParseEnvironment(string? text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var line in text!.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var name = line.Substring(0, separator).Trim();
                if (name.Length > 0) result[name] = line.Substring(separator + 1);
            }
            return result;
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
