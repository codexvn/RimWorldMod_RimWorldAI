using System;
using System.IO;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using Verse;

namespace RimWorldAgent
{
    public class GameComponent_RimWorldAgent : GameComponent
    {
        private AgentEngine? _engine;
        private ScribeDbStore? _dbStore;
        private bool _initialized;
        private int _lastTick;

        public GameComponent_RimWorldAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            if (_initialized) return;
            var settings = RimWorldAgentMod.Instance?.Settings;
            if (settings != null && !settings.AgentAutoRun) return;
            _initialized = true;

            var modRoot = Path.GetDirectoryName(
                typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var defaultProjectPath = Path.Combine(modRoot, "claude-sessions", "rimworld-agent");
            var projectPath = !string.IsNullOrEmpty(settings?.ProjectPath)
                ? Path.Combine(modRoot, settings!.ProjectPath)
                : defaultProjectPath;

            var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                ? Path.Combine(modRoot, settings!.SkillsDir)
                : Path.GetFullPath(Path.Combine(modRoot, "Skills"));

            var ccbDir = Path.GetFullPath(Path.Combine(modRoot, "cc-companion"));

            var gameHost = settings?.GameMcpHost ?? "localhost";
            var gamePort = settings?.GameMcpPort ?? 9877;

            var dbStore = new ScribeDbStore();
            var gameState = new DirectGameStateProvider();

            var cfg = new AgentEngineConfig
            {
                ProjectPath = projectPath,
                SkillsDir = skillsDir,
                McpUrl = $"http://{gameHost}:{gamePort}",
                McpPort = gamePort,
                AgentMcpPort = settings?.AgentMcpPort ?? 9878,
                CcbPort = 19999,
                CcbWsUrl = "ws://127.0.0.1:19999",
                ModelName = settings?.ModelName,
                CcbAutoStart = true,
                CcbAutoInstall = settings?.CcbAutoInstall ?? true,
                CcbDir = ccbDir,
                PlanSpeed = settings?.PlanSpeed ?? "paused",
                TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                ThinkingMode = settings?.ThinkingMode ?? "default",
                ThinkingEffort = settings?.ThinkingEffort ?? "medium",
                MaxThinkingTokens = settings?.MaxThinkingTokens ?? 0,
            };

            _engine = new AgentEngine(cfg, dbStore, gameState,
                logInfo: msg => Log.Message($"[agent-core] {msg}"),
                logError: msg => Log.Error($"[agent-core] {msg}"),
                logDebug: msg => Log.Message($"[agent-core] {msg}"));

            await _engine.InitAsync();
            _dbStore = dbStore;

            // 注入 CcbWebSocket 到 CCClient（供 UI 使用）
            if (_engine.CcbWs != null)
            {
                CCClient.SetSocket(_engine.CcbWs);
                WireChatDisplayUi(_engine.CcbWs);
            }

            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        /// <summary>将 CcbWebSocket 事件桥接到 ChatDisplayState（游戏内 UI）</summary>
        private static void WireChatDisplayUi(CcbWebSocket ws)
        {
            ws.OnAssistantText += text => ChatDisplayState.OnAssistantText(text);

            ws.OnToolUse += (toolId, toolName, input) =>
                ChatDisplayState.AddToolCall(toolId, toolName, input);

            ws.OnResult += (subtype, _) =>
            {
                ChatDisplayState.FinishStreaming();
                // 更新预算百分比供 UI 横幅使用
                if (TokenUsageTracker.TotalAllTokens > 0)
                {
                    var limit = RimWorldAgentMod.Instance?.Settings?.TokenBudgetLimit ?? 0;
                    ChatDisplayState.CurrentBudgetStatus = limit > 0
                        ? TokenUsageTracker.CheckBudget(limit)
                        : BudgetStatus.Ok;
                }
            };

            ws.OnAborted += () => ChatDisplayState.MarkLastAborted();
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (!_initialized || _engine == null) return;

            _engine.Tick();

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
    }
}
