using System;
using RimWorldAgent.Core.CcbManager;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    public class RimWorldAgentMod : Mod
    {
        public static RimWorldAgentMod Instance { get; private set; } = null!;
        public AgentModSettings Settings { get; private set; }

        public RimWorldAgentMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AgentModSettings>();
        }

        public override string SettingsCategory() => "RimWorld Agent";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            if (Find.CurrentMap != null)
            {
                GUI.color = Color.yellow;
                listing.Label("设置仅在主菜单生效，游戏内仅可查看。");
                GUI.color = Color.white;
                listing.Gap(8f);
            }

            // ==================== MCP 服务 ====================
            listing.Label("<b>MCP 服务</b>", tooltip: "Agent 通过 MCP 协议连接游戏获取数据和调用工具。SDK 只连 Agent 端点，游戏工具经此代理。");

            listing.Label("RimWorld MCP 主机");
            Settings.GameMcpHost = listing.TextEntry(Settings.GameMcpHost);
            listing.Label("  游戏 MCP 服务所在地址，默认 localhost");

            var gamePortStr = listing.TextEntry(Settings.GameMcpPort.ToString());
            listing.Label($"  RimWorld MCP 端口 (当前: {Settings.GameMcpPort})");
            listing.Label("  与 RimWorld MCP Mod 设置中的端口一致");
            if (int.TryParse(gamePortStr, out int gamePort) && gamePort > 0 && gamePort <= 65535)
                Settings.GameMcpPort = gamePort;

            var agentPortStr = listing.TextEntry(Settings.AgentMcpPort.ToString());
            listing.Label($"  Agent MCP 端口 (当前: {Settings.AgentMcpPort})");
            listing.Label("  SDK 通过此端口调用所有工具（内部+代理游戏）");
            if (int.TryParse(agentPortStr, out int agentPort) && agentPort > 0 && agentPort <= 65535)
                Settings.AgentMcpPort = agentPort;

            listing.Gap(4f);

            // ==================== 模型与思考 ====================
            listing.Label("<b>模型与思考</b>");

            listing.Label("模型名称 (如 claude-sonnet-4-6)");
            Settings.ModelName = listing.TextEntry(Settings.ModelName);

            // 思考模式（4 选 1）
            var modeLabels = new[] { "default (SDK 默认)", "disabled (禁用思考)", "adaptive (SDK 自控)", "fixed (固定预算)" };
            var modeValues = new[] { "default", "disabled", "adaptive", "fixed" };
            var modeIdx = Array.IndexOf(modeValues, Settings.ThinkingMode);
            if (modeIdx < 0) modeIdx = 0;
            if (listing.ButtonText($"思考模式: {modeLabels[modeIdx]}"))
            {
                modeIdx = (modeIdx + 1) % modeValues.Length;
                Settings.ThinkingMode = modeValues[modeIdx];
            }
            listing.Label("  default=跟随SDK | disabled=无思考 | adaptive=SDK自适应 | fixed=固定Token预算");

            // 思考力度（仅 adaptive 或 fixed 时显示）
            if (Settings.ThinkingMode == "adaptive" || Settings.ThinkingMode == "fixed")
            {
                listing.Gap(4f);
                var effortLabels = new[] { "medium (中)", "high (高)", "xhigh (极高)", "max (最大)" };
                var effortValues = new[] { "medium", "high", "xhigh", "max" };
                var effortIdx = Array.IndexOf(effortValues, Settings.ThinkingEffort);
                if (effortIdx < 0) effortIdx = 0;
                if (listing.ButtonText($"思考力度: {effortLabels[effortIdx]}"))
                {
                    effortIdx = (effortIdx + 1) % effortValues.Length;
                    Settings.ThinkingEffort = effortValues[effortIdx];
                }
            }

            // 最大 Token（仅 fixed 时显示）
            if (Settings.ThinkingMode == "fixed")
            {
                listing.Label("最大思考 Token (0=默认 8000)");
                var mtkStr = listing.TextEntry(Settings.MaxThinkingTokens.ToString());
                if (int.TryParse(mtkStr, out int mtk) && mtk >= 0)
                    Settings.MaxThinkingTokens = mtk;
            }

            listing.Gap(4f);

            // ==================== Token 预算 ====================
            listing.Label("<b>Token 预算</b>");

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

            // 累计用量（只读）
            listing.Gap(4f);
            var usage = TokenUsageTracker.GetCompactDisplay(Settings.TokenBudgetLimit);
            GUI.color = new Color(0.6f, 0.65f, 0.75f, 1f);
            listing.Label($"累计: {usage}");
            GUI.color = Color.white;

            // ==================== Agent 行为 ====================
            listing.Gap(12f);
            listing.Label("<b>Agent 行为</b>");

            listing.CheckboxLabeled("自动运行 Agent", ref Settings.AgentAutoRun,
                "开启后加载存档时自动启动。关闭则需手动在 EXE 模式运行。");

            var speedLabels = new[] { "paused (暂停)", "normal (1x)", "fast (2x)", "superfast (3x)", "ultrafast (最快)" };
            var speedValues = new[] { "paused", "normal", "fast", "superfast", "ultrafast" };
            var speedIdx = Array.IndexOf(speedValues, Settings.PlanSpeed);
            if (speedIdx < 0) speedIdx = 0;
            if (listing.ButtonText($"Plan 阶段速度: {speedLabels[speedIdx]}"))
            {
                speedIdx = (speedIdx + 1) % speedValues.Length;
                Settings.PlanSpeed = speedValues[speedIdx];
            }

            listing.Label("Skills 目录");
            listing.Label("  (留空使用默认 resource/Skills/)");
            Settings.SkillsDir = listing.TextEntry(Settings.SkillsDir);

            listing.Label("Project 目录 (会话存储)");
            listing.Label("  (留空使用默认 claude-sessions/rimworld-agent/)");
            Settings.ProjectPath = listing.TextEntry(Settings.ProjectPath);

            // ==================== CC Companion 依赖 ====================
            listing.Gap(12f);
            listing.Label("<b>CC Companion 依赖</b>");

            var modRoot = System.IO.Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
            var ccDir1 = System.IO.Path.GetFullPath(System.IO.Path.Combine(modRoot, "..", "..", "cc-companion"));
            var ccDir2 = System.IO.Path.GetFullPath(System.IO.Path.Combine(modRoot, "..", "..", "..", "..", "cc-companion"));
            var ccDir = System.IO.Directory.Exists(ccDir1) ? ccDir1 :
                        System.IO.Directory.Exists(ccDir2) ? ccDir2 : ccDir1;

            var installed = CompanionInstaller.IsInstalled(ccDir);
            var installing = CompanionInstaller.IsInstalling;
            var status = CompanionInstaller.InstallStatus;

            if (installing)
            {
                listing.Label($"  状态: 安装中...");
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
                "开启后，自动检查 cc-companion/node_modules，缺失则运行 npm install。");

            listing.End();
        }
    }
}
