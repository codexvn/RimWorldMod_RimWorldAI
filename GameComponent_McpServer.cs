using System;
using RimWorld;
using RimWorldMCP.AgentRuntime;
using RimWorldMCP.Tools;
using Verse;

namespace RimWorldMCP
{
    public class GameComponent_McpServer : GameComponent
    {
        private string _sessionId = "";
        public static string CurrentSessionId { get; private set; } = "";

        public GameComponent_McpServer(Game game)
        {
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            _sessionId = GenerateSessionId();
            CurrentSessionId = _sessionId;
            DeteriorationTracker.Reset();
            StartBridgeService();
            AttachMapUI();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            if (string.IsNullOrEmpty(_sessionId))
                _sessionId = GenerateSessionId();
            CurrentSessionId = _sessionId;
            DeteriorationTracker.Reset();
            StartBridgeService();
            AttachMapUI();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _sessionId, "mcpSessionId", "");
            TodoManager.ExposeData();
            TokenUsageTracker.ExposeData();
        }

        private static string GenerateSessionId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();

            // 进入游戏自动打开对话窗口
            if (AutoOpenChat)
            {
                AutoOpenChat = false;
                try
                {
                    if (Find.CurrentMap != null && !Find.WindowStack.IsOpen<Dialog_AiChat>())
                        Find.WindowStack.Add(new Dialog_AiChat());
                }
                catch { /* 窗口创建失败不影响游戏 */ }
            }

            McpLog.Flush();
            McpCommandQueue.ProcessPending();
            Tool_AdvanceTick.ProcessPending();
            Tool_AdvanceTick.LowSpeedTick();
            BridgeLifecycle.Tick();
            AgentRuntimeTick();
            McpOssUploader.ProcessPendingUploads();
            McpCommandQueue.ProcessDeferredCleanup();

            // 自动追踪殖民者（帧末，不影响其他处理）
            CameraHelper.AutoTrackColonistsTick();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
        }

        private void StartBridgeService()
        {
            // 停止上一局可能残留的桥接
            BridgeLifecycle.Stop();
            TodoManager.Clear();

            // 刷新 Tool 注册表（游戏状态变化后重新创建 Tool 实例）
            McpServiceManager.RefreshTools();

            // 初始化 Agent Runtime
            var sessionDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(GameComponent_McpServer).Assembly.Location) ?? ".",
                "claude-sessions", $"rimworld-{_sessionId}");
            McpLog.Info($"[agent-runtime] sessionDir = {sessionDir}");
            TaskBoard.SessionDir = sessionDir;

            McpLog.Info($"[session] ID = {_sessionId}");

            // 启动桥接器
            _ = BridgeLifecycle.StartAsync(_sessionId);
        }

        /// <summary>新游戏/加载后自动打开对话窗口</summary>
        internal static bool AutoOpenChat;

        private static void AttachMapUI()
        {
            var map = Find.CurrentMap;
            if (map == null) return;
            // 防止重复添加
            foreach (var c in map.components)
                if (c is MapComponent_McpUI) return;
            map.components.Add(new MapComponent_McpUI(map));
            AutoOpenChat = true;
        }

        /// <summary>每帧运行 Agent Runtime 调度逻辑。</summary>
        private static void AgentRuntimeTick()
        {
            if (Find.CurrentMap == null) return;

            // 更新 Colony Load Score
            Scheduler.Tick();

            // L3 事件立即唤醒: Combat / Medic
            if (Harmony.NotificationBus.HighDangerPending && !AgentOrchestrator.IsCombatActive)
            {
                // 收集高危事件中的 Agent 路由
                bool needsCombat = false, needsMedic = false;
                var pending = Harmony.NotificationBus.Drain();
                foreach (var n in pending)
                {
                    var route = Harmony.NotificationBus.GetEventAgent(n.Type, n.DangerLabel, n.Label);
                    if (route == EventRoute.Combat) needsCombat = true;
                    if (route == EventRoute.Medic) needsMedic = true;
                    // 重新入队（Drain 已经取走了）
                    Harmony.NotificationBus.Pending.Enqueue(n);
                }

                if (needsCombat && Find.TickManager != null && !Find.TickManager.Paused)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                    McpLog.Info("[agent-runtime] L3 战斗事件 → 暂停游戏，唤醒 Combat Agent");
                }

                if (needsCombat)
                {
                    var config = AgentConfigs.Combat;
                    McpLog.Info($"[agent-runtime] 立即唤醒 Combat (Load={Scheduler.LoadScore})");
                    var prompt = ContextBuilder.Build(config);
                    McpLog.Info($"[agent-runtime] Combat prompt 已构建 ({prompt.Length} 字符)");
                    AgentOrchestrator.BeginAgent(config.Name);
                }
                if (needsMedic)
                {
                    var config = AgentConfigs.Medic;
                    McpLog.Info($"[agent-runtime] 立即唤醒 Medic (L3 医疗)");
                    var prompt = ContextBuilder.Build(config);
                    McpLog.Info($"[agent-runtime] Medic prompt 已构建 ({prompt.Length} 字符)");
                    AgentOrchestrator.BeginAgent(config.Name);
                }

                Harmony.NotificationBus.HighDangerPending = false;
            }

            // 检查每个 Agent 是否应该定时唤醒
            foreach (var config in AgentConfigs.All)
            {
                if (config.Name == "combat") continue; // Combat 仅事件触发

                bool shouldWake = false;

                // 定时触发
                if (config.IntervalGameHours > 0 && Scheduler.ShouldWake(config.Name, config.IntervalGameHours))
                    shouldWake = true;

                // 每天触发
                if (config.TriggerDaily && AgentOrchestrator.IsNewDay(config.Name))
                    shouldWake = true;

                if (shouldWake)
                {
                    McpLog.Info($"[agent-runtime] 唤醒 {config.Name} (Load={Scheduler.LoadScore}, Mode={Scheduler.Mode})");
                    var prompt = ContextBuilder.Build(config);
                    // TODO: 通过 CCB 发送 prompt 到 Claude SDK
                    McpLog.Info($"[agent-runtime] {config.Name} prompt 已构建 ({prompt.Length} 字符)");
                    AgentOrchestrator.BeginAgent(config.Name);
                }
            }

            // Combat 兜底: 检测是否需要提示退出
            if (AgentOrchestrator.IsCombatActive)
            {
                AgentOrchestrator.CombatRoundCount++;
                int mapEnemies = 0;
                foreach (var pawn in Find.CurrentMap.mapPawns.AllPawnsSpawned)
                    if (pawn.Faction != null && pawn.Faction.HostileTo(Faction.OfPlayer) && !pawn.Downed)
                        mapEnemies++;

                if (AgentOrchestrator.CombatRoundCount >= AgentOrchestrator.CombatMaxRounds)
                {
                    McpLog.Warn("[agent-runtime] Combat 超时，强制退出");
                    AgentOrchestrator.IsCombatActive = false;
                    AgentOrchestrator.CombatRoundCount = 0;
                }
                else if (mapEnemies == 0 && AgentOrchestrator.CombatRoundCount >= AgentOrchestrator.CombatRemindRound)
                {
                    McpLog.Info($"[agent-runtime] 提示 Combat 退出 (Round {AgentOrchestrator.CombatRoundCount})");
                }
            }
        }

    }
}
