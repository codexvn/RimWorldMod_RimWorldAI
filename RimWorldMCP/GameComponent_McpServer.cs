using System;
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
            StartMcpSession();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            if (string.IsNullOrEmpty(_sessionId)) _sessionId = GenerateSessionId();
            CurrentSessionId = _sessionId;
            DeteriorationTracker.Reset();
            StartMcpSession();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _sessionId, "mcpSessionId", "");
            TodoManager.ExposeData();
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

        private void StartMcpSession()
        {
            TodoManager.Clear();
            McpServiceManager.RefreshTools();
            McpLog.Info($"[session] ID = {_sessionId}");
        }
    }
}
