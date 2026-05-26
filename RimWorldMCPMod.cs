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
            float h = 560f;
            h += Settings.CCBAutoStart ? 70f : 170f;
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

            // ====== CC 桥接 ======
            listing.Label("CC 桥接");
            listing.Gap(2f);

            listing.Label("连接地址 (WebSocket)");
            listing.Label("ws://{host}:{port}，默认连本地 Companion");
            Settings.CCBHost = listing.TextEntry(Settings.CCBHost);
            var ccPortStr = listing.TextEntry(Settings.CCBPort.ToString());
            if (int.TryParse(ccPortStr, out int ccPort) && ccPort > 0 && ccPort <= 65535)
                Settings.CCBPort = ccPort;

            listing.Gap(6f);
            listing.CheckboxLabeled("自动启动本地 Companion", ref Settings.CCBAutoStart,
                "开启后，游戏加载时自动 spawn Node.js 子进程。");

            if (Settings.CCBAutoStart)
            {
                listing.Label("Token (可选)");
                Settings.CCBAuthToken = listing.TextEntry(Settings.CCBAuthToken);
                listing.Label("模型 (留空用 settings.json)");
                Settings.CCBModelName = listing.TextEntry(Settings.CCBModelName);
            }

            listing.Gap(6f);
            var installed = BridgeLifecycle.IsCompanionInstalled();
            var installing = BridgeLifecycle.IsInstalling;
            var status = BridgeLifecycle.InstallStatus;

            if (installing)
            {
                listing.Label("安装中...");
                if (!string.IsNullOrEmpty(status))
                    listing.Label($"  {status}");
            }
            else if (installed)
            {
                listing.Label("Claude Code 状态: 已安装");
                if (listing.ButtonText("重新安装"))
                    BridgeLifecycle.InstallCompanion();
                if (listing.ButtonText("卸载 Claude Code 依赖"))
                    BridgeLifecycle.UninstallCompanion();
            }
            else
            {
                listing.Label($"Companion 状态: 未安装{(string.IsNullOrEmpty(status) ? "" : $" ({status})")}");
                if (!installing && listing.ButtonText("安装 Claude Code 依赖"))
                    BridgeLifecycle.InstallCompanion();
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
