using System;
using RimWorldAgent.Core.CcbManager;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public class RimWorldAgentMod : Mod
    {
        private const float HttpMcpServerCardHeight = 280f;
        private const float StdioMcpServerCardHeight = 450f;

        public static RimWorldAgentMod Instance { get; private set; } = null!;
        public AgentModSettings Settings { get; private set; }
        private Vector2 _scrollPos;

        public RimWorldAgentMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AgentModSettings>();
        }

        public override string SettingsCategory() => "RimWorld Agent";

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

        private static bool IsCustomMcpServerNameValid(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var trimmed = name.Trim();
            if (string.Equals(trimmed, "agent", StringComparison.OrdinalIgnoreCase)) return false;
            for (var i = 0; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
                    continue;
                return false;
            }
            return true;
        }

        private static bool IsHttpUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private void EnsureCustomMcpServerCollections()
        {
            if (Settings.CustomMcpServers == null)
                Settings.CustomMcpServers = new System.Collections.Generic.List<CustomMcpServerSetting>();
        }

        private string NextCustomMcpServerName(string baseName = "my-server")
        {
            for (var i = 1; i < 1000; i++)
            {
                var name = i == 1 ? baseName : $"{baseName}-{i}";
                var exists = false;
                foreach (var server in Settings.CustomMcpServers)
                {
                    if (server != null && string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists) return name;
            }
            return baseName;
        }

        private static string NormalizeCustomMcpServerTypeForUi(string type)
        {
            var normalized = (type ?? "http").Trim().ToLowerInvariant();
            if (normalized == "sse") return "sse";
            if (normalized == "stdio" || normalized == "npx") return "stdio";
            return "http";
        }

        private static string CustomMcpServerTypeLabel(string type)
        {
            switch (NormalizeCustomMcpServerTypeForUi(type))
            {
                case "sse": return "SSE";
                case "stdio": return "STDIO（本地命令）";
                default: return "HTTP";
            }
        }

        private static bool IsStdioMcpServer(CustomMcpServerSetting server)
            => NormalizeCustomMcpServerTypeForUi(server?.Type ?? "http") == "stdio";

        private static string DrawTextArea(Listing_Standard listing, string text, float height)
        {
            var rect = listing.GetRect(height);
            return Widgets.TextArea(rect, text ?? "");
        }

        private static string TruncateText(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? "";
            return text.Substring(0, maxLength - 1) + "…";
        }

        private static string GetCustomMcpServerSummary(CustomMcpServerSetting server)
        {
            if (server == null) return "未配置";
            if (IsStdioMcpServer(server))
            {
                var command = string.IsNullOrWhiteSpace(server.Command) ? "npx" : server.Command.Trim();
                var args = (server.ArgsText ?? "").Trim();
                return TruncateText(string.IsNullOrEmpty(args) ? command : $"{command} {args}", 64);
            }
            return TruncateText(string.IsNullOrWhiteSpace(server.Url) ? "未填写地址" : server.Url.Trim(), 64);
        }

        private static float GetCustomMcpServerCardHeight(CustomMcpServerSetting server)
        {
            var isStdio = server != null && IsStdioMcpServer(server);
            var height = isStdio ? StdioMcpServerCardHeight : HttpMcpServerCardHeight;
            if (server == null) return height;
            if (!IsCustomMcpServerNameValid(server.Name ?? "")) height += 24f;
            if (server.Enabled && !isStdio && !IsHttpUrl(server.Url ?? ""))
                height += 24f;
            return height;
        }

        private bool DrawCustomMcpServerCard(Listing_Standard listing, CustomMcpServerSetting server, int index)
        {
            var cardRect = listing.GetRect(GetCustomMcpServerCardHeight(server));
            var bgColor = server.Enabled
                ? new Color(0.08f, 0.09f, 0.12f, 0.72f)
                : new Color(0.06f, 0.06f, 0.07f, 0.55f);
            Widgets.DrawBoxSolid(cardRect, bgColor);
            Widgets.DrawBox(cardRect);

            var headerRect = new Rect(cardRect.x + 1f, cardRect.y + 1f, cardRect.width - 2f, 30f);
            Widgets.DrawBoxSolid(headerRect, server.Enabled
                ? new Color(0.12f, 0.16f, 0.2f, 0.95f)
                : new Color(0.1f, 0.1f, 0.11f, 0.9f));

            var oldFont = Text.Font;
            var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = server.Enabled ? new Color(0.72f, 0.82f, 0.95f, 1f) : new Color(0.55f, 0.55f, 0.58f, 1f);
            var displayName = string.IsNullOrWhiteSpace(server.Name) ? "未命名服务" : server.Name.Trim();
            var status = server.Enabled ? "启用" : "停用";
            Widgets.Label(new Rect(headerRect.x + 8f, headerRect.y, headerRect.width - 16f, headerRect.height),
                $"#{index + 1}  {displayName}  ·  {CustomMcpServerTypeLabel(server.Type)}  ·  {status}  ·  {GetCustomMcpServerSummary(server)}");
            GUI.color = Color.white;
            Text.Font = oldFont;
            Text.Anchor = oldAnchor;

            var innerRect = new Rect(cardRect.x + 8f, cardRect.y + 38f, cardRect.width - 16f, cardRect.height - 46f);
            var inner = new Listing_Standard();
            inner.Begin(innerRect);

            inner.CheckboxLabeled("启用", ref server.Enabled);

            inner.Label("服务名（字母、数字、点号、下划线、短横线；不能为 agent）");
            server.Name = inner.TextEntry(server.Name ?? "").Trim();
            if (!IsCustomMcpServerNameValid(server.Name))
            {
                GUI.color = Color.red;
                inner.Label("  服务名无效或与内置 agent 冲突。无效服务不会写入 .mcp.json。");
                GUI.color = Color.white;
            }

            var typeValues = new[] { "http", "sse", "stdio" };
            var currentType = NormalizeCustomMcpServerTypeForUi(server.Type);
            var typeIdx = Array.IndexOf(typeValues, currentType);
            if (typeIdx < 0) typeIdx = 0;
            if (inner.ButtonText($"传输: {CustomMcpServerTypeLabel(typeValues[typeIdx])}"))
            {
                typeIdx = (typeIdx + 1) % typeValues.Length;
                server.Type = typeValues[typeIdx];
            }
            else
            {
                server.Type = typeValues[typeIdx];
            }

            if (IsStdioMcpServer(server))
            {
                inner.Label("启动命令（默认 npx；也可填 uvx、node、python、docker 等）");
                server.Command = inner.TextEntry(string.IsNullOrWhiteSpace(server.Command) ? "npx" : server.Command).Trim();

                inner.Label("命令参数（空格分隔，支持引号；可留空；不包含启动命令本身）");
                server.ArgsText = inner.TextEntry(server.ArgsText ?? "").Trim();

                inner.Label("环境变量（每行 KEY=VALUE，可留空；会写入 .mcp.json）");
                server.EnvText = DrawTextArea(inner, server.EnvText ?? "", 72f);
            }
            else
            {
                inner.Label(server.Type == "sse" ? "SSE 地址" : "HTTP 地址");
                server.Url = inner.TextEntry(server.Url ?? "").Trim();
                if (server.Enabled && !IsHttpUrl(server.Url))
                {
                    GUI.color = Color.yellow;
                    inner.Label("  请输入 http:// 或 https:// 开头的完整地址。无效服务不会写入 .mcp.json。");
                    GUI.color = Color.white;
                }
            }

            inner.Label("超时 (ms)");
            var timeoutStr = inner.TextEntry(server.Timeout.ToString());
            if (int.TryParse(timeoutStr, out var timeout) && timeout > 0)
                server.Timeout = timeout;

            var deleted = inner.ButtonText("删除此 MCP 服务");
            inner.End();
            return deleted;
        }

        private void DrawCustomMcpServersSection(Listing_Standard listing)
        {
            EnsureCustomMcpServerCollections();
            DrawSectionHeader(listing, "自定义 MCP 服务");
            GUI.color = new Color(0.6f, 0.65f, 0.75f, 1f);
            listing.Label("内置 agent MCP 服务会始终保留，自定义服务不能命名为 agent。");
            listing.Label("这些服务会在 Agent 启动时合并到 Project 目录下的 .mcp.json，修改后需重新启动 Agent 生效。");
            listing.Label("支持 HTTP/SSE 远程 MCP，也支持 STDIO 本地命令 MCP（如 npx/uvx/node/python/docker）。环境变量会写入 .mcp.json，请谨慎填写敏感信息。");
            GUI.color = Color.white;

            for (var i = 0; i < Settings.CustomMcpServers.Count; i++)
            {
                var server = Settings.CustomMcpServers[i] ?? new CustomMcpServerSetting();
                Settings.CustomMcpServers[i] = server;

                listing.Gap(10f);
                if (DrawCustomMcpServerCard(listing, server, i))
                {
                    Settings.CustomMcpServers.RemoveAt(i);
                    i--;
                }
            }

            listing.Gap(8f);
            if (listing.ButtonText("添加 HTTP/SSE MCP 服务"))
            {
                var name = NextCustomMcpServerName();
                Settings.CustomMcpServers.Add(new CustomMcpServerSetting
                {
                    Enabled = true,
                    Name = name,
                    Type = "http",
                    Url = "http://localhost:3000/mcp",
                    Command = "npx",
                    Timeout = 300000
                });            }
            if (listing.ButtonText("添加 STDIO MCP 服务"))
            {
                var name = NextCustomMcpServerName("stdio-server");
                Settings.CustomMcpServers.Add(new CustomMcpServerSetting
                {
                    Enabled = true,
                    Name = name,
                    Type = "stdio",
                    Command = "npx",
                    ArgsText = "-y ",
                    Timeout = 300000
                });            }
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            EnsureCustomMcpServerCollections();
            var customHeight = 0f;
            foreach (var server in Settings.CustomMcpServers)
                customHeight += GetCustomMcpServerCardHeight(server) + 10f;
            var h = 1120f + customHeight;
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, h);
            Widgets.BeginScrollView(inRect, ref _scrollPos, viewRect);
            var listing = new Listing_Standard();
            listing.Begin(viewRect);

            if (Find.CurrentMap != null)
            {
                GUI.color = Color.yellow;
                listing.Label("设置仅在主菜单生效，游戏内仅可查看。");
                GUI.color = Color.white;
                listing.Gap(8f);
            }

            // ==================== MCP 服务 ====================
            DrawSectionHeader(listing, "MCP 服务");

            listing.Label("游戏 MCP 服务地址");
            Settings.GameMcpHost = listing.TextEntry(Settings.GameMcpHost);

            listing.Label("游戏 MCP 端口");
            var gamePortStr = listing.TextEntry(Settings.GameMcpPort.ToString());
            if (int.TryParse(gamePortStr, out int gamePort) && gamePort > 0 && gamePort <= 65535)
                Settings.GameMcpPort = gamePort;

            listing.Label("Agent MCP 端口 (SDK 连接)");
            var agentPortStr = listing.TextEntry(Settings.AgentMcpPort.ToString());
            if (int.TryParse(agentPortStr, out int agentPort) && agentPort > 0 && agentPort <= 65535)
                Settings.AgentMcpPort = agentPort;

            DrawCustomMcpServersSection(listing);

            // ==================== 模型与思考 ====================
            DrawSectionHeader(listing, "模型与思考");

            listing.Label("模型名称 (如 claude-sonnet-4-6)");
            Settings.ModelName = listing.TextEntry(Settings.ModelName);

            var modeLabels = new[] { "adaptive (引导深度)", "disabled (禁用思考)" };
            var modeValues = new[] { "adaptive", "disabled" };
            var modeIdx = Array.IndexOf(modeValues, Settings.ThinkingMode);
            if (modeIdx < 0) modeIdx = 0;
            if (listing.ButtonText($"思考模式: {modeLabels[modeIdx]}"))
            {
                modeIdx = (modeIdx + 1) % modeValues.Length;
                Settings.ThinkingMode = modeValues[modeIdx];
            }

            listing.Gap(4f);
            var effortLabels = new[] { "low (低)", "medium (中)", "high (高)", "xhigh (极高)", "max (最大)" };
            var effortValues = new[] { "low", "medium", "high", "xhigh", "max" };
            var effortIdx = Array.IndexOf(effortValues, Settings.ThinkingEffort);
            if (effortIdx < 0) effortIdx = 2; // 默认 "high"
            if (listing.ButtonText($"思考力度: {effortLabels[effortIdx]}"))
            {
                effortIdx = (effortIdx + 1) % effortValues.Length;
                Settings.ThinkingEffort = effortValues[effortIdx];
            }

            // ==================== Token 预算 ====================
            DrawSectionHeader(listing, "Token 预算");

            listing.Label("预算上限 (K, 0=不限制)");
            var limitKStr = listing.TextEntry((Settings.TokenBudgetLimit / 1000).ToString());
            if (long.TryParse(limitKStr, out long limitK) && limitK >= 0)
                Settings.TokenBudgetLimit = limitK * 1000;

            var actionLabels = new[] { "Block (阻止)", "Warn (警告)" };
            var actionValues = new[] { "Block", "Warn" };
            var actionIdx = Array.IndexOf(actionValues, Settings.TokenBudgetAction);
            if (actionIdx < 0) actionIdx = 0;
            if (listing.ButtonText($"超出行为: {actionLabels[actionIdx]}"))
            {
                actionIdx = (actionIdx + 1) % actionValues.Length;
                Settings.TokenBudgetAction = actionValues[actionIdx];
            }

            listing.Gap(4f);
            var usage = TokenUsageTracker.GetCompactDisplay(Settings.TokenBudgetLimit);
            GUI.color = new Color(0.6f, 0.65f, 0.75f, 1f);
            listing.Label($"累计: {usage}");
            GUI.color = Color.white;

            // ==================== Agent 行为 ====================
            DrawSectionHeader(listing, "Agent 行为");

            listing.CheckboxLabeled("自动运行 Agent", ref Settings.AgentAutoRun,
                "开启后加载存档时自动启动。");

            var speedLabels = new[] { "paused (暂停)", "normal (1x)", "fast (2x)", "superfast (3x)", "ultrafast (最快)" };
            var speedValues = new[] { "paused", "normal", "fast", "superfast", "ultrafast" };
            var speedIdx = Array.IndexOf(speedValues, Settings.PlanSpeed);
            if (speedIdx < 0) speedIdx = 0;
            if (listing.ButtonText($"Plan 阶段速度: {speedLabels[speedIdx]}"))
            {
                speedIdx = (speedIdx + 1) % speedValues.Length;
                Settings.PlanSpeed = speedValues[speedIdx];
            }

            listing.Label("Skills 目录 (留空用默认)");
            Settings.SkillsDir = listing.TextEntry(Settings.SkillsDir);
            if (listing.ButtonText("管理 Skills / Skills.d"))
                Find.WindowStack.Add(new Dialog_SkillManager());

            listing.Label("Project 目录 (留空用默认)");
            Settings.ProjectPath = listing.TextEntry(Settings.ProjectPath);

            // ==================== UI Bridge ====================
            DrawSectionHeader(listing, "UI 桥接 (WebSocket)");

            listing.Label("监听地址");
            Settings.BridgeHost = listing.TextEntry(Settings.BridgeHost);

            listing.Label("监听端口");
            var bpStr = listing.TextEntry(Settings.BridgePort.ToString());
            if (int.TryParse(bpStr, out int bp) && bp > 0 && bp <= 65535)
                Settings.BridgePort = bp;

            // ==================== CC Companion ====================
            DrawSectionHeader(listing, "CC Companion 依赖");

            var asmDir = System.IO.Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
            var ccDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(asmDir, "cc-companion"));

            var installed = CompanionInstaller.IsInstalled(ccDir);
            var installing = CompanionInstaller.IsInstalling;
            var status = CompanionInstaller.InstallStatus;

            if (installing)
            {
                listing.Label("  状态: 安装中...");
                if (!string.IsNullOrEmpty(status)) listing.Label($"    {status}");
            }
            else if (installed)
            {
                listing.Label("  状态: 已安装 (node_modules 就绪)");
                if (listing.ButtonText("  重新安装 (npm install)"))
                    CompanionInstaller.Install(ccDir);
                if (listing.ButtonText("  卸载 (删除 node_modules)"))
                    CompanionInstaller.Uninstall(ccDir);
            }
            else
            {
                listing.Label($"  状态: 未安装{(string.IsNullOrEmpty(status) ? "" : $" ({status})")}");
                if (!installing && listing.ButtonText("  安装 (npm install)"))
                    CompanionInstaller.Install(ccDir);
            }

            listing.CheckboxLabeled("自动安装 (加载时)", ref Settings.CcbAutoInstall,
                "开启后自动检查 cc-companion/node_modules，缺失则运行 npm install。");

            DrawSectionHeader(listing, "日志");

            listing.CheckboxLabeled("☐ SDK 交互日志 (sdk-log.txt)", ref Settings.LogSdkMessages,
                "开启后 companion 将 SDK 双向通信记录写入 project 目录下的 sdk-log.txt。");
            listing.CheckboxLabeled("☐ C#↔CCB WS 日志 (ccb-ws-log.txt)", ref Settings.LogCcbWsMessages,
                "开启后 C# 将 WebSocket 收发 JSON 记录写入 project 目录下的 ccb-ws-log.txt。");

            listing.End();
            Widgets.EndScrollView();
        }
    }
}
