using System;
using UnityEngine;
using Verse;
using RimWorldMCP.Tools;

namespace RimWorldMCP
{
    public class RimWorldMCPMod : Mod
    {
        public static RimWorldMCPMod Instance { get; private set; } = null!;
        public McpModSettings Settings { get; private set; }
        private Vector2 _scrollPos;
        private Vector2 _jsonScrollPos;
        private bool _showSecrets;

        private static string MaskSecret(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            if (s.Length <= 4) return "****";
            return s.Substring(0, 4) + "****" + s.Substring(s.Length - 4);
        }

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

        private static void DrawSectionHeader(Listing_Standard listing, string title)
        {
            listing.Gap(4f);
            var rect = listing.GetRect(22f);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y + 10f, rect.width, 1f),
                new Color(0.25f, 0.25f, 0.3f, 0.6f));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.45f, 0.5f, 0.6f, 1f);
            Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width, 18f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            listing.Gap(2f);
        }

        private float CalcContentHeight()
        {
            float h = 0f;
            // 调试
            h += 60f;
            // MCP 服务器
            h += 100f;
            // OSS
            h += 70f;
            if (Settings.OssEnabled)
            {
                h += 200f;
                if (_showSecrets) h += 80f;
                if (Settings.OssUseSignedUrl) h += 50f;
            }
            return h;
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var h = CalcContentHeight();
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, h);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            // ==================== 调试 ====================
            DrawSectionHeader(listing, "调试");
            listing.Label($"日志级别: {McpModSettings.LogLevelLabels[(int)Settings.LogLevel]}");
            if (listing.ButtonText("切换"))
            {
                var next = (int)Settings.LogLevel + 1;
                if (next >= McpModSettings.LogLevelLabels.Length) next = 0;
                Settings.LogLevel = (LogLevel)next;
                McpLog.MinLogLevel = Settings.LogLevel;
            }

            // ==================== MCP 服务器 ====================
            DrawSectionHeader(listing, "MCP 服务器");
            listing.Label("监听地址 (localhost / 0.0.0.0 / 内网 IP)");
            Settings.McpHost = listing.TextEntry(Settings.McpHost);
            listing.Label("端口");
            var portStr = listing.TextEntry(Settings.McpPort.ToString());
            if (int.TryParse(portStr, out int port) && port > 0 && port <= 65535)
                Settings.McpPort = port;

            // ==================== OSS 截图上传 ====================
            DrawSectionHeader(listing, "OSS 截图上传");
            listing.CheckboxLabeled("启用 OSS 自动上传", ref Settings.OssEnabled,
                "截图后自动上传到阿里云 OSS 并返回公网 URL。");

            if (Settings.OssEnabled)
            {
                listing.Label("Endpoint (ServiceUrl)");
                listing.Label("  示例: https://oss-cn-beijing.aliyuncs.com");
                Settings.OssServiceUrl = listing.TextEntry(Settings.OssServiceUrl);

                listing.Label("Bucket 名称");
                Settings.OssBucketName = listing.TextEntry(Settings.OssBucketName);

                var showKeys = listing.ButtonText(_showSecrets ? "隐藏密钥" : "显示密钥");
                if (showKeys) _showSecrets = !_showSecrets;
                if (_showSecrets)
                {
                    listing.Label("AccessKey ID");
                    Settings.OssAccessKey = listing.TextEntry(Settings.OssAccessKey);
                    listing.Label("AccessKey Secret");
                    Settings.OssSecretKey = listing.TextEntry(Settings.OssSecretKey);
                }
                else
                {
                    listing.Label($"AccessKey ID: {MaskSecret(Settings.OssAccessKey)}");
                    listing.Label($"AccessKey Secret: {MaskSecret(Settings.OssSecretKey)}");
                }

                listing.Gap(12f);
                listing.CheckboxLabeled("使用签名 URL", ref Settings.OssUseSignedUrl,
                    "生成有时效的预签名 URL，Bucket 无需设为公开读。关闭则返回公开 URL。");

                if (Settings.OssUseSignedUrl)
                {
                    listing.Label("签名有效期（小时，默认 24）");
                    var expiryStr = listing.TextEntry(Settings.OssSignedUrlExpiryHours.ToString());
                    if (int.TryParse(expiryStr, out int expiry) && expiry > 0 && expiry <= 168)
                        Settings.OssSignedUrlExpiryHours = expiry;
                }
            }

            listing.End();
            Widgets.EndScrollView();
        }
    }
}
