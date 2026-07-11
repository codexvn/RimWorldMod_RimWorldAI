using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldMCP.Constants;
using RimWorldMCP.Harmony;
using RimWorldMCP.MapRendering;
using RimWorldMCP.Tools;
using Verse;

namespace RimWorldMCP
{
    public class GameComponent_McpServer : GameComponent
    {
        private static string _sessionId = "";
        private int _lastTickPush;
        public static string CurrentSessionId { get; private set; } = "";

        public GameComponent_McpServer(Game game) { }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            // sessionId 由 Agent 通过 set_session_id 设置，不主动生成
            CurrentSessionId = _sessionId;
            DeteriorationTracker.Reset();
            TrappedColonistTracker.Reset();
            StartMcpSession();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            // sessionId 由 Scribe 从存档读取，或由 Agent set_session_id 设置
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

        /// <summary>由 Agent 调用，更新 ACP sessionId。空字符串表示清空。</summary>
        public static void SetSessionId(string id)
        {
            _sessionId = id ?? "";
            CurrentSessionId = _sessionId;
            McpLog.Info($"[session] 外部设置 ID = {_sessionId}");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            McpLog.Flush();
            McpCommandQueue.ProcessPending();
            Tool_AdvanceTick.ProcessPending();
            Tool_AdvanceTick.LowSpeedTick();
            Tool_SetPrisonerPolicy.ProcessPendingAutoPolicies();
            McpOssUploader.ProcessPendingUploads();
            McpCommandQueue.ProcessDeferredCleanup();
            CameraHelper.AutoTrackColonistsTick();
            AiObservationOverlay.Tick(Find.CurrentMap);

            // 每 60 tick (~1s) 推送一次游戏状态到 MCP 客户端
            var currentTick = Find.TickManager?.TicksGame ?? 0;
            if (currentTick - _lastTickPush >= 60)
            {
                _lastTickPush = currentTick;
                PushTickEvent();
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
                var json = System.Text.Json.JsonSerializer.Serialize(new { type = "tick", tick });
                McpServiceManager.Host?.SendEvent(McpChannels.GameTick, json);
            }
            catch (Exception ex) { McpLog.Warn($"tick 推送失败: {ex}"); }
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
                NotificationBus.Enqueue(new Notification
                {
                    Type = NotificationType.Message,
                    DangerLabel = "被困",
                    Label = $"{info.Name} 路径阻断",
                    Text = info.Detail,
                    Tick = info.DetectedTick
                });

                McpLog.Info($"[trapped] {info.Name}: PathBlocked @ ({info.PosX},{info.PosZ}) — {info.Detail}");
            }
        }
    }
}
