using System;
using System.Linq;
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
            GlobalModelUsageStore.Load();
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
            h += 60f;
            // MCP 服务器
            h += 100f;
            // CC 桥接
            h += 300f;
            if (Settings.CCBAutoStart) h += 310f; // 连接认证+模型+思考+JSON
            if (Settings.CCBThinkingMode == ThinkingMode.Adaptive) h += 30f;
            if (Settings.CCBThinkingMode == ThinkingMode.Fixed) h += 40f;
            h += 80f; // 安装按钮
            // 地图渲染
            h += 100f;
            // Token 预算
            h += 140f;
            if (Settings.TokenBudgetExceedAction == TokenBudgetExceedAction.Warn) h += 50f;
            // 全局用量
            var globalModels = GlobalModelUsageStore.AllModels;
            h += 60f + globalModels.Count * 22f;
            // OSS
            h += 70f; // 标题 + 启用开关（始终可见）
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

            // ==================== 工具行为 ====================
            DrawSectionHeader(listing, "工具行为");
            var cameraTools = ToolRegistry.CameraToolNames;
            var tooltip = "AI 调用带坐标参数的工具时，自动将游戏视角平滑移动到目标位置。"
                + (cameraTools.Count > 0
                    ? $"\n\n支持自动移动的工具（{cameraTools.Count} 个）：\n" + string.Join(", ", cameraTools)
                    : "");
            listing.CheckboxLabeled("调用工具时自动移动视角", ref Settings.AutoMoveCamera, tooltip);

            // ==================== MCP 服务器 ====================
            DrawSectionHeader(listing, "MCP 服务器");
            listing.Label("监听地址 (localhost / 0.0.0.0 / 内网 IP)");
            Settings.McpHost = listing.TextEntry(Settings.McpHost);
            listing.Label("端口");
            var portStr = listing.TextEntry(Settings.McpPort.ToString());
            if (int.TryParse(portStr, out int port) && port > 0 && port <= 65535)
                Settings.McpPort = port;

            // ==================== CC 桥接 ====================
            DrawSectionHeader(listing, "CC 桥接");

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.5f, 0.5f, 0.55f, 1f);
            listing.Label("  本地监听（companion 进程绑定地址）");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Settings.CCBHost = listing.TextEntry(Settings.CCBHost);
            var ccPortStr = listing.TextEntry(Settings.CCBPort.ToString());
            if (int.TryParse(ccPortStr, out int ccPort) && ccPort > 0 && ccPort <= 65535)
                Settings.CCBPort = ccPort;

            listing.Gap(6f);

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.5f, 0.5f, 0.55f, 1f);
            listing.Label("  远程连接（C# WebSocket 连接目标）");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Settings.CCBRemoteHost = listing.TextEntry(Settings.CCBRemoteHost);
            var ccRemotePortStr = listing.TextEntry(Settings.CCBRemotePort.ToString());
            if (int.TryParse(ccRemotePortStr, out int ccRemotePort) && ccRemotePort > 0 && ccRemotePort <= 65535)
                Settings.CCBRemotePort = ccRemotePort;

            listing.Gap(6f);
            listing.CheckboxLabeled("自动启动 Mod 附带的 Claude Code", ref Settings.CCBAutoStart,
                "开启后，游戏加载时自动 spawn Node.js 子进程。");

            if (Settings.CCBAutoStart)
            {
                listing.Gap(4f);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.5f, 0.5f, 0.55f, 1f);
                listing.Label("  连接认证");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Settings.CCBAuthToken = listing.TextEntry(Settings.CCBAuthToken);

                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.5f, 0.5f, 0.55f, 1f);
                listing.Label("  模型名称");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Settings.CCBModelName = listing.TextEntry(Settings.CCBModelName);

                listing.Gap(4f);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.5f, 0.5f, 0.55f, 1f);
                listing.Label("  思考模式");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                listing.Label($"    {McpModSettings.ThinkingModeLabels[(int)Settings.CCBThinkingMode]}");
                if (listing.ButtonText("    切换"))
                {
                    var next = (int)Settings.CCBThinkingMode + 1;
                    if (next >= McpModSettings.ThinkingModeLabels.Length) next = 0;
                    Settings.CCBThinkingMode = (ThinkingMode)next;
                }
                if (Settings.CCBThinkingMode == ThinkingMode.Adaptive)
                {
                    var curEffort = Array.IndexOf(McpModSettings.ThinkingEffortLabels, Settings.CCBThinkingEffort);
                    if (curEffort < 0) curEffort = 1;
                    listing.Label($"    Effort: {Settings.CCBThinkingEffort}");
                    if (listing.ButtonText("    切换 Effort"))
                    {
                        curEffort = (curEffort + 1) % McpModSettings.ThinkingEffortLabels.Length;
                        Settings.CCBThinkingEffort = McpModSettings.ThinkingEffortLabels[curEffort];
                    }
                }
                else if (Settings.CCBThinkingMode == ThinkingMode.Fixed)
                {
                    listing.Label("    Max Thinking Tokens");
                    var tokenStr = listing.TextEntry(Settings.CCBMaxThinkingTokens > 0
                        ? Settings.CCBMaxThinkingTokens.ToString() : "10000");
                    if (int.TryParse(tokenStr, out var parsedTokens) && parsedTokens > 0)
                        Settings.CCBMaxThinkingTokens = parsedTokens;
                }

                // --- MCP 服务器配置 ---
                listing.Gap(12f);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.5f, 0.5f, 0.55f, 1f);
                listing.Label("  MCP 服务器配置 (.mcp.json)");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                var textAreaRect = listing.GetRect(180f);
                var jsonViewRect = new Rect(0f, 0f, textAreaRect.width - 16f, Mathf.Max(
                    GUI.skin.textArea.CalcHeight(new GUIContent(Settings.CCBProjectSettingsJson), textAreaRect.width), 180f));
                Widgets.BeginScrollView(textAreaRect, ref _jsonScrollPos, jsonViewRect);
                Settings.CCBProjectSettingsJson = GUI.TextArea(jsonViewRect, Settings.CCBProjectSettingsJson);
                Widgets.EndScrollView();
                if (listing.ButtonText("  复位为默认值"))
                    Settings.CCBProjectSettingsJson = BridgeLifecycle.BuildMcpJson(Settings.McpPort);
            }

            // --- 安装状态 ---
            var installed = BridgeLifecycle.IsCompanionInstalled();
            var installing = BridgeLifecycle.IsInstalling;
            var status = BridgeLifecycle.InstallStatus;
            if (installing)
            {
                listing.Label("  安装中...");
                if (!string.IsNullOrEmpty(status)) listing.Label($"    {status}");
            }
            else if (installed)
            {
                listing.Label("  状态: 已安装");
                if (listing.ButtonText("  重新安装"))
                    BridgeLifecycle.InstallCompanion();
                if (listing.ButtonText("  卸载"))
                    BridgeLifecycle.UninstallCompanion();
            }
            else
            {
                listing.Label($"  状态: 未安装{(string.IsNullOrEmpty(status) ? "" : $" ({status})")}");
                if (!installing && listing.ButtonText("  安装"))
                    BridgeLifecycle.InstallCompanion();
            }

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

            // ==================== Token 预算 ====================
            DrawSectionHeader(listing, "Token 预算");
            listing.Label("预算上限 (0=无限制)");
            var limitStr = listing.TextEntry(Settings.TokenBudgetLimit > 0
                ? (Settings.TokenBudgetLimit / 1_000_000f).ToString("F1") + "M"
                : "0");
            if (limitStr == "0" || limitStr == "0M")
                Settings.TokenBudgetLimit = 0;
            else if (limitStr.EndsWith("M") || limitStr.EndsWith("m"))
            {
                if (float.TryParse(limitStr.TrimEnd('M', 'm'), out float mVal) && mVal > 0)
                    Settings.TokenBudgetLimit = (long)(mVal * 1_000_000);
            }
            else if (long.TryParse(limitStr, out long rawVal))
                Settings.TokenBudgetLimit = rawVal;
            listing.Gap(4f);

            var actionLabels = McpModSettings.BudgetActionLabels;
            int actionIdx = (int)Settings.TokenBudgetExceedAction;
            listing.Label($"超出行为: {actionLabels[actionIdx]}");
            if (listing.ButtonText("切换"))
            {
                Settings.TokenBudgetExceedAction = actionIdx == 0
                    ? TokenBudgetExceedAction.Warn
                    : TokenBudgetExceedAction.Block;
            }
            listing.Gap(4f);

            if (Settings.TokenBudgetExceedAction == TokenBudgetExceedAction.Warn)
            {
                listing.Label("Webhook URL (超出时 POST 通知)");
                Settings.TokenBudgetWebhookUrl = listing.TextEntry(Settings.TokenBudgetWebhookUrl);
            }

            // ==================== 全局用量 ====================
            DrawSectionHeader(listing, "全局用量汇总（所有存档）");
            var globalModels = GlobalModelUsageStore.AllModels;
            if (globalModels.Count == 0)
            {
                listing.Label("  暂无记录");
            }
            else
            {
                var headerRect = listing.GetRect(20f);
                Text.Font = GameFont.Tiny;
                float[] colX = { headerRect.x, headerRect.x + 140f, headerRect.x + 210f, headerRect.x + 280f,
                    headerRect.x + 350f, headerRect.x + 400f, headerRect.x + 460f };
                GUI.color = new Color(0.5f, 0.5f, 0.5f, 1f);
                Widgets.Label(new Rect(colX[0], headerRect.y, 130f, 20f), "模型");
                Widgets.Label(new Rect(colX[1], headerRect.y, 65f, 20f), "输入");
                Widgets.Label(new Rect(colX[2], headerRect.y, 65f, 20f), "输出");
                Widgets.Label(new Rect(colX[3], headerRect.y, 65f, 20f), "缓存命中");
                Widgets.Label(new Rect(colX[4], headerRect.y, 65f, 20f), "缓存创建");
                Widgets.Label(new Rect(colX[5], headerRect.y, 65f, 20f), "对话数");
                Widgets.Label(new Rect(colX[6], headerRect.y, 65f, 20f), "总 Token");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                foreach (var m in globalModels)
                {
                    var rowRect = listing.GetRect(20f);
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(0.7f, 0.7f, 0.7f, 1f);
                    Widgets.Label(new Rect(colX[0], rowRect.y, 130f, 20f), m.Key);
                    Widgets.Label(new Rect(colX[1], rowRect.y, 65f, 20f), FormatK(m.Value.InputTokens));
                    Widgets.Label(new Rect(colX[2], rowRect.y, 65f, 20f), FormatK(m.Value.OutputTokens));
                    Widgets.Label(new Rect(colX[3], rowRect.y, 65f, 20f), FormatK(m.Value.CacheReadTokens));
                    Widgets.Label(new Rect(colX[4], rowRect.y, 65f, 20f), FormatK(m.Value.CacheCreateTokens));
                    Widgets.Label(new Rect(colX[5], rowRect.y, 65f, 20f), m.Value.RequestCount.ToString());
                    Widgets.Label(new Rect(colX[6], rowRect.y, 65f, 20f),
                        FormatK(m.Value.InputTokens + m.Value.OutputTokens + m.Value.CacheCreateTokens));
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
            }

            // 清空按钮
            if (globalModels.Count > 0)
            {
                if (listing.ButtonText("清空全局用量"))
                {
                    Find.WindowStack.Add(new Dialog_Confirm("确认清空所有存档的全局用量记录？此操作不可撤销。",
                        () => GlobalModelUsageStore.Clear()));
                }
            }

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

        private static string FormatK(long v)
        {
            if (v >= 1_000_000) return (v / 1_000_000f).ToString("F1") + "M";
            if (v >= 1_000) return (v / 1000f).ToString("F0") + "K";
            return v.ToString();
        }
    }
}
