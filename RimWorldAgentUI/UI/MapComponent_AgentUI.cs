using System;
using Verse;

namespace RimWorldAgent
{
    /// <summary>
    /// 游戏加载后自动连接 UIMessageBus，维持常驻连接。
    /// Dialog_AiChat 通过静态 Bridge 属性复用此连接。
    /// </summary>
    public class MapComponent_AgentUI : MapComponent
    {
        private static BridgeClient? _bridge;
        private static WebUIHttpServer? _httpServer;
        private static bool _initialized;
        private static readonly object _lock = new();

        public static BridgeClient? Bridge => _bridge;
        public static bool IsConnected => _bridge?.IsConnected ?? false;

        public MapComponent_AgentUI(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            SafeLog.Flush();
            if (!_initialized)
            {
                _initialized = true;
                InitAsync();
            }
        }

        private void InitAsync()
        {
            try
            {
                var bridgeUrl = AgentUIMod.Instance?.Settings?.BridgeWsUrl ?? "ws://127.0.0.1:19999";
                var bridgePort = new Uri(bridgeUrl).Port;

                // HTTP 服务 — 立即启动，不依赖 UIMessageBus 连接状态
                var httpPort = AgentUIMod.Instance?.Settings?.WebUIPort ?? 19997;
                _httpServer = new WebUIHttpServer(httpPort, bridgePort);
                _httpServer.Start();
                Log.Message($"[AgentUI] WebUI: http://localhost:{httpPort}");

                // UIMessageBus 连接 — fire-and-forget，失败自动指数退避重连
                _bridge = new BridgeClient(bridgeUrl);
                _bridge.OnMessage += msg => ChatDisplayState.ProcessMessage(msg);
                _ = _bridge.ConnectAsync();
            }
            catch (Exception ex) { Log.Warning($"[AgentUI] 初始化失败: {ex.Message}"); }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            _bridge?.Dispose();
            _bridge = null;
            _httpServer?.Dispose();
            _httpServer = null;
            _initialized = false;
        }
    }
}
