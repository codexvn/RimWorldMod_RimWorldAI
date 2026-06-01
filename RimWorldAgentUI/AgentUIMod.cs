using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public class AgentUIMod : Mod
    {
        public static AgentUIMod Instance { get; private set; } = null!;
        public AgentUISettings Settings { get; private set; }

        public AgentUIMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AgentUISettings>();
        }

        public override string SettingsCategory() => "RimWorld Agent UI";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("<b>连接</b>", tooltip: "RimWorld Agent 核心服务的 UIMessageBus 地址");
            listing.Label("UIMessageBus WS 地址");
            Settings.BridgeWsUrl = listing.TextEntry(Settings.BridgeWsUrl);
            listing.Label("  默认 ws://127.0.0.1:19999");

            listing.Label("WebUI HTTP 端口");
            var portStr = listing.TextEntry(Settings.WebUIPort.ToString());
            if (int.TryParse(portStr, out int p) && p > 0 && p <= 65535)
                Settings.WebUIPort = p;
            listing.Label($"  浏览器访问 http://localhost:{Settings.WebUIPort}");

            listing.End();
        }
    }
}
