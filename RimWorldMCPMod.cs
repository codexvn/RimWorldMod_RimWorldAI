using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    public class RimWorldMCPMod : Mod
    {
        public static RimWorldMCPMod Instance { get; private set; } = null!;
        public McpModSettings Settings { get; private set; }
        private Vector2 _scrollPos;

        public RimWorldMCPMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<McpModSettings>();
            McpLog.MinLogLevel = Settings.LogLevel;
        }

        public override string SettingsCategory()
        {
            return "RimWorld MCP";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            float h = 600f;
            if (Settings.BridgeType > 0) h += 200f;
            if (Settings.OssEnabled) h += 220f;
            if (Settings.OssEnabled && Settings.OssUseSignedUrl) h += 50f;

            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, h);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            // ====== 日志级别 ======
            listing.Label($"日志级别: {McpModSettings.LogLevelLabels[(int)Settings.LogLevel]}");
            if (listing.ButtonText("切换"))
            {
                var next = (int)Settings.LogLevel + 1;
                if (next >= McpModSettings.LogLevelLabels.Length) next = 0;
                Settings.LogLevel = (LogLevel)next;
                McpLog.MinLogLevel = Settings.LogLevel;
            }
            listing.Gap(24f);

            // ====== MCP 服务器 ======
            listing.Label("MCP 服务器");
            listing.Gap(2f);

            listing.Label("监听地址 (localhost / 0.0.0.0 / 内网 IP)");
            Settings.McpHost = listing.TextEntry(Settings.McpHost);

            listing.Label("端口");
            var portStr = listing.TextEntry(Settings.McpPort.ToString());
            if (int.TryParse(portStr, out int port) && port > 0 && port <= 65535)
                Settings.McpPort = port;

            listing.Gap(24f);

            // ====== 桥接器 ======
            listing.Label("桥接器类型");
            if (listing.ButtonText(Settings.BridgeType < McpModSettings.BridgeTypeLabels.Length
                ? McpModSettings.BridgeTypeLabels[Settings.BridgeType]
                : "未知"))
            {
                Settings.BridgeType = (Settings.BridgeType + 1) % McpModSettings.BridgeTypeLabels.Length;
            }

            if (Settings.BridgeType > 0)
            {
                // OpenClaw
                listing.Label("Gateway WebSocket URL");
                listing.Label("示例: ws://127.0.0.1:18789");
                Settings.BridgeUrl = listing.TextEntry(Settings.BridgeUrl);

                listing.Label("Token");
                Settings.BridgeToken = listing.TextEntry(Settings.BridgeToken);

                listing.Label("Password");
                Settings.BridgePassword = listing.TextEntry(Settings.BridgePassword);
            }

            listing.Gap(24f);

            // ====== OSS ======
            listing.CheckboxLabeled("启用 OSS 上传", ref Settings.OssEnabled,
                "开启后，截图将自动上传到阿里云 OSS");

            if (Settings.OssEnabled)
            {
                listing.Gap(12f);

                listing.Label("Endpoint");
                listing.Label("示例: https://oss-cn-beijing.aliyuncs.com");
                Settings.OssServiceUrl = listing.TextEntry(Settings.OssServiceUrl);

                listing.Label("Bucket 名称");
                Settings.OssBucketName = listing.TextEntry(Settings.OssBucketName);

                listing.Label("AccessKey ID");
                Settings.OssAccessKey = listing.TextEntry(Settings.OssAccessKey);

                listing.Label("AccessKey Secret");
                Settings.OssSecretKey = listing.TextEntry(Settings.OssSecretKey);

                listing.Gap(12f);
                listing.CheckboxLabeled("使用签名 URL", ref Settings.OssUseSignedUrl,
                    "生成有时效的预签名 URL，Bucket 无需设为公开读。关闭则返回公开 URL。");

                if (Settings.OssUseSignedUrl)
                {
                    listing.Label("签名有效期（小时）");
                    var expiryStr = listing.TextEntry(Settings.OssSignedUrlExpiryHours.ToString());
                    if (int.TryParse(expiryStr, out int expiryHours) && expiryHours > 0)
                        Settings.OssSignedUrlExpiryHours = expiryHours;
                }
            }

            listing.End();
            Widgets.EndScrollView();
        }
    }
}
