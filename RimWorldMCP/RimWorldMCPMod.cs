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

        // ===== 辅助：绘制分级标题 =====
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

        // ===== 高度估算 =====
        private float CalcContentHeight()
        {
            float h = 0f;

            // 调试
            h += 60f;
            // 工具行为
            h += 84f;
            // MCP 服务器
            h += 100f;
            // 地图渲染
            h += 150f;
            // OSS
            h += 50f; // 标题
            h += 40f; // 启用开关（始终可见）
            if (Settings.OssEnabled)
            {
                h += 280f;  // Endpoint+Bucket+密钥按钮+签名开关+兜底
                if (_showSecrets) h += 90f;
                if (Settings.OssUseSignedUrl) h += 60f;
            }

            // 兜底余量，防止静态估算偏差导致末尾控件不可见
            h += 80f;

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

            // ==================== 工具行为 ====================
            DrawSectionHeader(listing, "工具行为");
            var cameraTools = ToolRegistry.CameraToolNames;
            var tooltip = "AI 调用带坐标参数的工具时，自动将游戏视角平滑移动到目标位置。"
                + (cameraTools.Count > 0
                    ? $"\n\n支持自动移动的工具（{cameraTools.Count} 个）：\n" + string.Join(", ", cameraTools)
                    : "");
            listing.CheckboxLabeled("调用工具时自动移动视角", ref Settings.AutoMoveCamera, tooltip);
            listing.CheckboxLabeled("AI 查询时显示观察覆盖层", ref Settings.AutoObserveOverlay,
                "AI 调用搜索/网格等查询工具时，在地图上短暂显示彩色半透明标记，指示 AI 正在关注的区域。");
            listing.CheckboxLabeled("自动跟踪殖民者", ref Settings.AutoTrackColonists,
                "游戏运行时自动将视角平滑移动到殖民者聚集位置。战斗时约 0.5 秒跟随一次，和平时期约 2 秒检查一次。");

            // ==================== MCP 服务器 ====================
            DrawSectionHeader(listing, "MCP 服务器");
            listing.Label("监听地址 (localhost / 0.0.0.0 / 内网 IP)");
            Settings.McpHost = listing.TextEntry(Settings.McpHost);
            listing.Label("端口");
            var portStr = listing.TextEntry(Settings.McpPort.ToString());
            if (int.TryParse(portStr, out int port) && port > 0 && port <= 65535)
                Settings.McpPort = port;

            // ==================== 地图渲染 ====================
            DrawSectionHeader(listing, "地图渲染");
            listing.Label($"分块尺寸: {Settings.ChunkWidth} x {Settings.ChunkHeight}");
            if (listing.ButtonText("切换分块宽度"))
            {
                var sizes = new[] { 16, 24, 32, 48, 64 };
                var idx = Array.IndexOf(sizes, Settings.ChunkWidth);
                Settings.ChunkWidth = sizes[(idx + 1) % sizes.Length];
                Settings.ChunkHeight = Settings.ChunkWidth;
            }
            listing.Label($"压缩方法: {McpModSettings.CompressionMethodLabels[(int)Settings.GridCompression]}");
            if (listing.ButtonText("切换压缩方法"))
            {
                var next = (int)Settings.GridCompression + 1;
                if (next >= McpModSettings.CompressionMethodLabels.Length) next = 0;
                Settings.GridCompression = (CompressionMethod)next;
            }
            listing.Label("  RLE 和行引用可降低 Token 消耗 60-80%");
            listing.Gap(4f);
            listing.Label($"网格查询模式: {McpModSettings.GridQueryModeLabels[(int)Settings.GridQueryMode]}");
            if (listing.ButtonText("切换查询模式"))
            {
                var next = (int)Settings.GridQueryMode + 1;
                if (next >= McpModSettings.GridQueryModeLabels.Length) next = 0;
                Settings.GridQueryMode = (GridQueryMode)next;
            }
            listing.Label("  Chunk: 按分块查询（压缩输出）| 坐标: 按坐标范围查询（逐行输出）");
            listing.Gap(4f);

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
