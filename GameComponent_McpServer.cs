using System;
using RimWorldMCP.Tools;
using Verse;

namespace RimWorldMCP
{
    public class GameComponent_McpServer : GameComponent
    {
        private string _sessionId = "";
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
        }

        private void StartMcpSession()
        {
            TodoManager.Clear();
            McpServiceManager.RefreshTools();
            McpLog.Info($"[session] ID = {_sessionId}");
        }
    }
}
