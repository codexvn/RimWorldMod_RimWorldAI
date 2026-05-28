using System;
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

    }
}
