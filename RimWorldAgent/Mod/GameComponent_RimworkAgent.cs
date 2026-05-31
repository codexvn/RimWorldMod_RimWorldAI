using System.IO;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using Verse;

namespace RimWorldAgent
{
    public class GameComponent_RimWorldAgent : GameComponent
    {
        private AgentEngine? _engine;
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

            var modRoot = Path.GetDirectoryName(typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var defaultSessionDir = Path.Combine(modRoot, "claude-sessions", "rimworld-agent");
            var sessionDir = !string.IsNullOrEmpty(settings?.SessionDir)
                ? Path.Combine(modRoot, settings!.SessionDir)
                : defaultSessionDir;

            var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                ? Path.Combine(modRoot, settings!.SkillsDir)
                : Path.GetFullPath(Path.Combine(modRoot, "Skills"));

            var ccbDir = Path.GetFullPath(Path.Combine(modRoot, "cc-companion"));

            var cfg = new AgentEngineConfig
            {
                SessionDir = sessionDir,
                SkillsDir = skillsDir,
                McpUrl = $"http://localhost:{settings?.McpPort ?? 9877}",
                McpPort = settings?.McpPort ?? 9877,
                AgentMcpPort = settings?.AgentMcpPort ?? 9878,
                CcbPort = settings?.CCBPort ?? 19999,
                CcbWsUrl = $"ws://{settings?.CCBRemoteHost ?? "127.0.0.1"}:{settings?.CCBRemotePort ?? 19999}",
                CcbToken = settings?.CCBAuthToken,
                ModelName = settings?.CCBModelName,
                CcbAutoStart = settings?.CCBAutoStart ?? true,
                CcbAutoInstall = settings?.CCBAutoInstall ?? true,
                CcbDir = ccbDir,
                PlanSpeed = settings?.PlanSpeed ?? "paused",
                TokenBudgetLimit = settings?.TokenBudgetLimit ?? 0,
                ThinkingEffort = settings?.CCBThinkingEffort ?? "medium",
                MaxThinkingTokens = settings?.CCBMaxThinkingTokens ?? 0,
            };

            _engine = new AgentEngine(cfg,
                logInfo: msg => Log.Message($"[agent-core] {msg}"),
                logError: msg => Log.Error($"[agent-core] {msg}"),
                logDebug: msg => Log.Message($"[agent-core] {msg}"));

            await _engine.InitAsync();

            EventForwarder.SetCcbSocket(_engine.CcbWs);
            EventForwarder.SendGameConnected();

            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (!_initialized || _engine == null) return;

            _engine.Tick();
            EventForwarder.Tick();

            if (Find.CurrentMap == null) return;

            var settings = RimWorldAgentMod.Instance?.Settings;
            _lastTick++;
            var intervalMs = settings?.LoopIntervalMs ?? 2000;
            if (intervalMs < 100) intervalMs = 100;
            var threshold = intervalMs / 16;
            if (threshold < 1) threshold = 1;
            if (_lastTick < threshold) return;
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
        }
    }
}
