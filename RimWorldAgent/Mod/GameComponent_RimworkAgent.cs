using System;
using System.IO;
using System.Threading.Tasks;
using RimWorldAgent.Core;
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
            // 先杀上一次的 CCB 残留（返回主菜单时 Game.Dispose 不通知 GameComponent）
            ShutdownEngine();

            var settings = RimWorldAgentMod.Instance?.Settings;
            if (settings != null && !settings.AgentAutoRun) return;
            _initialized = true;

            // 重载存档时清空上一轮残留数据
            ToolDispatcher.ResetTaskCount();

            var modRoot = Path.GetDirectoryName(
                typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var defaultProjectPath = Path.Combine(modRoot, "claude-sessions", "rimworld-agent");
            var projectPath = !string.IsNullOrEmpty(settings?.ProjectPath)
                ? Path.Combine(modRoot, settings!.ProjectPath)
                : defaultProjectPath;

            var skillsDir = !string.IsNullOrEmpty(settings?.SkillsDir)
                ? Path.Combine(modRoot, settings!.SkillsDir)
                : Path.GetFullPath(Path.Combine(modRoot, "Skills"));

            var asmDir = Path.GetDirectoryName(
                typeof(GameComponent_RimWorldAgent).Assembly.Location) ?? ".";
            var ccbDir = Path.GetFullPath(Path.Combine(asmDir, "cc-companion"));

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
                CcbPort = 19998,
                CcbWsUrl = "ws://127.0.0.1:19998",
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

            // log 回调可能从后台线程（CCB stdout/stderr、WS ReceiveLoop、MCP HTTP）触发，
            // SafeLog 通过 ConcurrentQueue 入队，主线程 GameComponentUpdate 中 Flush 安全写入 Verse.Log
            _engine = new AgentEngine(cfg, dbStore, gameState,
                logInfo: msg => SafeLog.Info($"[agent-core] {msg}"),
                logError: msg => SafeLog.Error($"[agent-core] {msg}"),
                logDebug: msg => SafeLog.Info($"[agent-core] {msg}"),
                logWarn: msg => SafeLog.Warning($"[agent-core] {msg}"));

            await _engine.InitAsync();
            _dbStore = dbStore;

            // 启动 BridgeBus（Web 前端 WS 服务器，默认端口 19999）
            if (settings?.BridgeHost != "disabled")
            {
                var bridgePort = settings?.BridgePort ?? 19999;
                BridgeBus.Start(bridgePort);
            }

            // UI 总线：SDK 消息 → BridgeBus 广播 + 客户端消息 → CCB
            if (_engine.CcbWs != null)
                WireBridgeBus(_engine.CcbWs);

            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            SafeLog.Flush();
            if (!_initialized || _engine == null) return;

            _engine.Tick();
            BridgeBus.IsReady = _engine.CcbWs?.IsReady ?? false;

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

        /// <summary>CcbWebSocket → BridgeBus 中继：SDK 消息 → 所有客户端，客户端消息 → CCB</summary>
        private static void WireBridgeBus(CcbWebSocket ws)
        {
            // SDK 消息 → BridgeBus 广播
            ws.OnRawSdkMessage += json => BridgeBus.PushSdkMessage(json);

            // 客户端 chat/abort → CCB（Web UI 和游戏内 Dialog 都走 BridgeBus）
            BridgeBus.OnChat += async (text, thinking) =>
            {
                var limit = RimWorldAgentMod.Instance?.Settings?.TokenBudgetLimit ?? 0;
                if (limit > 0 && TokenUsageTracker.TotalAllTokens >= limit)
                {
                    BridgeBus.PushGameEvent(UiMessage.Error($"Token 预算已用尽 ({TokenUsageTracker.TotalAllTokens}/{limit})"));
                    return;
                }
                // 回显用户消息到所有客户端
                BridgeBus.PushGameEvent(UiMessage.User(text));
                await ws.SendChat("bus", text, thinking);
            };
            BridgeBus.OnAbort += async () => await ws.SendAbort();
            BridgeBus.IsReady = ws.IsReady;
        }

        private void ShutdownEngine()
        {
            CoreLog.Info("[agent-mod] 返回主菜单，开始关闭 Agent 和 CCB...");
            BridgeBus.Stop();
            try
            {
                _engine?.Dispose();
                _engine = null;
                _initialized = false;
                CoreLog.Info("[agent-mod] Agent 和 CCB 已关闭");
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[agent-mod] 关闭 Agent 失败: {ex.Message}");
            }
        }

    }
}
