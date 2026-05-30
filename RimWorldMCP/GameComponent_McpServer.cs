using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldMCP.Harmony;
using RimWorldMCP.Tools;
using Verse;

namespace RimWorldMCP
{
    public class GameComponent_McpServer : GameComponent
    {
        private string _sessionId = "";
        private int _lastTickPush;
        public static string CurrentSessionId { get; private set; } = "";

        public GameComponent_McpServer(Game game) { }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            _sessionId = GenerateSessionId();
            CurrentSessionId = _sessionId;
            DeteriorationTracker.Reset();
            TrappedColonistTracker.Reset();
            StartMcpSession();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            if (string.IsNullOrEmpty(_sessionId)) _sessionId = GenerateSessionId();
            CurrentSessionId = _sessionId;
            DeteriorationTracker.Reset();
            TrappedColonistTracker.Reset();
            StartMcpSession();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _sessionId, "mcpSessionId", "");
        }

        private static string GenerateSessionId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            McpLog.Flush();
            McpCommandQueue.ProcessPending();
            Tool_AdvanceTick.ProcessPending();
            Tool_AdvanceTick.LowSpeedTick();
            McpOssUploader.ProcessPendingUploads();
            McpCommandQueue.ProcessDeferredCleanup();
            CameraHelper.AutoTrackColonistsTick();

            // 每 60 tick (~1s) 推送一次游戏状态到 MCP 客户端
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            if (currentTick - _lastTickPush >= 60)
            {
                _lastTickPush = currentTick;
                PushTickEvent();
                PushWorldState();
            }

            // 殖民者被困检测（每 200 tick，~3.3s @1x）
            var map = Find.CurrentMap;
            if (map != null)
            {
                var trapped = TrappedColonistTracker.CheckAndNotify(map);
                if (trapped != null && trapped.Count > 0)
                    PushTrappedNotification(trapped);
            }
        }

        private static void PushTickEvent()
        {
            try
            {
                var tick = Find.TickManager?.TicksGame ?? 0;
                var speed = Find.TickManager?.CurTimeSpeed ?? Verse.TimeSpeed.Normal;
                var paused = Find.TickManager?.Paused ?? false;
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "tick",
                    tick,
                    speed = speed.ToString(),
                    paused
                });
                SimpleMspServer.McpServiceHost.Instance?.SendEvent("game/tick", json);
            }
            catch (Exception ex) { Verse.Log.Warning($"[McpServer] tick 推送失败: {ex.Message}"); }
        }

        private static void PushWorldState()
        {
            try
            {
                var map = Find.CurrentMap;
                if (map == null) return;

                int colonists = 0, idle = 0, enemies = 0, downed = 0;
                float foodDays = 0f;
                int medicine = 0;

                var colonistList = PawnsFinder.AllMaps_FreeColonistsSpawned;
                colonists = colonistList.Count;
                foreach (var c in colonistList)
                    if (c.mindState?.IsIdle == true) idle++;

                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p.Faction == null || !p.Faction.HostileTo(Faction.OfPlayer)) continue;
                    if (p.Downed) downed++; else enemies++;
                }

                var res = map.resourceCounter;
                foreach (var kv in res.AllCountedAmounts)
                {
                    if (kv.Key.IsNutritionGivingIngestible && kv.Key.ingestible?.HumanEdible == true)
                        foodDays += kv.Value * kv.Key.ingestible.CachedNutrition / (colonists * 1.6f);
                    if (kv.Key.IsMedicine) medicine += kv.Value;
                }

                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "world-state", colonists, idle, enemies, downed, foodDays, medicine
                });
                SimpleMspServer.McpServiceHost.Instance?.SendEvent("game/world-state", json);
            }
            catch (Exception ex) { Verse.Log.Warning($"[McpServer] world-state 推送失败: {ex.Message}"); }
        }

        private void StartMcpSession()
        {
            McpServiceManager.RefreshTools();
            McpLog.Info($"[session] ID = {_sessionId}");
        }

        private static void PushTrappedNotification(List<TrappedColonistTracker.TrappedColonistInfo> trapped)
        {
            foreach (var info in trapped)
            {
                bool isCritical = info.TrapType == "Downed" && info.Detail.Contains("无人可达");
                string dangerLabel = isCritical ? "被困-紧急" : "被困";

                NotificationBus.Enqueue(new Notification
                {
                    Type = NotificationType.Message,
                    DangerLabel = dangerLabel,
                    Label = $"{info.Name} 被困 ({info.TrapType})",
                    Text = info.Detail,
                    Tick = info.DetectedTick
                });

                McpLog.Info($"[trapped] {info.Name}: {info.TrapType} @ ({info.PosX},{info.PosZ}) — {info.Detail}");
            }
        }
    }
}
