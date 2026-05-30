using System;
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

            // ==================== CCB 桥接 ====================
            listing.Label("<b>CC 桥接</b>", tooltip: "Claude Code 伴随进程");

            listing.Label("本地监听（companion 进程绑定地址）");
            Settings.CCBHost = listing.TextEntry(Settings.CCBHost);
            var ccPortStr = listing.TextEntry(Settings.CCBPort.ToString());
            if (int.TryParse(ccPortStr, out int ccPort) && ccPort > 0 && ccPort <= 65535)
                Settings.CCBPort = ccPort;

            listing.Label("远程连接（C# WebSocket 连接目标）");
            Settings.CCBRemoteHost = listing.TextEntry(Settings.CCBRemoteHost);
            var ccRemotePortStr = listing.TextEntry(Settings.CCBRemotePort.ToString());
            if (int.TryParse(ccRemotePortStr, out int ccRemotePort) && ccRemotePort > 0 && ccRemotePort <= 65535)
                Settings.CCBRemotePort = ccRemotePort;

            listing.Gap(6f);
            listing.CheckboxLabeled("自动启动 companion 进程", ref Settings.CCBAutoStart,
                "开启后，游戏加载时自动 spawn Node.js 子进程。");

            listing.Label("认证 Token");
            Settings.CCBAuthToken = listing.TextEntry(Settings.CCBAuthToken);

            listing.Label("模型名称");
            Settings.CCBModelName = listing.TextEntry(Settings.CCBModelName);

            listing.Gap(12f);
            listing.Label("<b>.mcp.json</b>", tooltip: "MCP 服务器配置，CCB SDK 据此发现 Tool。");

            var mcpPortStr = listing.TextEntry(Settings.McpPort.ToString());
            listing.Label($"  MCP 游戏服务端口 (当前: {Settings.McpPort})");
            if (int.TryParse(mcpPortStr, out int mcpPort) && mcpPort > 0 && mcpPort <= 65535)
                Settings.McpPort = mcpPort;

            var agentMcpPortStr = listing.TextEntry(Settings.AgentMcpPort.ToString());
            listing.Label($"  Agent 内部工具端口 (当前: {Settings.AgentMcpPort})");
            if (int.TryParse(agentMcpPortStr, out int agentMcpPort) && agentMcpPort > 0 && agentMcpPort <= 65535)
                Settings.AgentMcpPort = agentMcpPort;

            listing.Label($"  → rimworld → :{Settings.McpPort}/mcp");
            listing.Label($"  → agent    → :{Settings.AgentMcpPort}/mcp");
            listing.Gap(4f);

            // ==================== Agent 行为 ====================
            listing.Gap(12f);
            listing.Label("<b>Agent 行为</b>");

            listing.CheckboxLabeled("自动运行 Agent", ref Settings.AgentAutoRun,
                "开启后加载存档时自动启动 Agent Runtime。关闭则需要手动在 EXE 模式运行。");

            // Plan 阶段速度选择（按钮循环）
            var speedLabels = new[] { "paused (暂停)", "normal (1x)", "fast (2x)", "superfast (3x)", "ultrafast (最快)" };
            var speedValues = new[] { "paused", "normal", "fast", "superfast", "ultrafast" };
            var speedIdx = Array.IndexOf(speedValues, Settings.PlanSpeed);
            if (speedIdx < 0) speedIdx = 0;
            if (listing.ButtonText($"Plan 阶段速度: {speedLabels[speedIdx]}"))
            {
                speedIdx = (speedIdx + 1) % speedValues.Length;
                Settings.PlanSpeed = speedValues[speedIdx];
            }

            listing.Label("Agent Loop 间隔 (ms)");
            var intervalStr = listing.TextEntry(Settings.LoopIntervalMs.ToString());
            if (int.TryParse(intervalStr, out int interval) && interval >= 1000 && interval <= 60000)
                Settings.LoopIntervalMs = interval;

            listing.Label("Skills 目录");
            listing.Label("  (留空使用默认 resource/Skills/)");
            Settings.SkillsDir = listing.TextEntry(Settings.SkillsDir);

            listing.Label("Session 目录");
            listing.Label("  (留空使用默认 claude-sessions/rimworld-agent/)");
            Settings.SessionDir = listing.TextEntry(Settings.SessionDir);

            // ==================== CC Companion 安装 ====================
            listing.Gap(12f);
            listing.Label("<b>CC Companion 依赖</b>");

            var modRoot = System.IO.Path.GetDirectoryName(typeof(RimWorldAgentMod).Assembly.Location) ?? ".";
            // publish: ../../cc-companion, source: ../../../../cc-companion
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

            listing.CheckboxLabeled("自动安装 (加载时)", ref Settings.CCBAutoInstall,
                "开启后，自动检查 cc-companion/node_modules，缺失则运行 npm install。");

            listing.End();
        }
    }
}
