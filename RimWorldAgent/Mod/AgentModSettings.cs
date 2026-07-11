using System.Collections.Generic;
using Verse;

namespace RimWorldAgent
{
    public class AcpBackendSetting : IExposable
    {
        public bool Enabled = true;
        public string Id = "";
        public string DisplayName = "";
        public string Type = "custom";
        public string Command = "";
        public string ArgsText = "";
        public string WorkingDirectory = "";
        public string EnvText = "";

        public bool IsBundled => string.Equals(Type, "bundled", System.StringComparison.OrdinalIgnoreCase);

        public void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "enabled", true);
            Scribe_Values.Look(ref Id, "id", "");
            Scribe_Values.Look(ref DisplayName, "displayName", "");
            Scribe_Values.Look(ref Type, "type", "custom");
            Scribe_Values.Look(ref Command, "command", "");
            Scribe_Values.Look(ref ArgsText, "argsText", "");
            Scribe_Values.Look(ref WorkingDirectory, "workingDirectory", "");
            Scribe_Values.Look(ref EnvText, "envText", "");
        }

    }

    public class AgentModSettings : ModSettings
    {
        // Token 预算
        public long TokenBudgetLimit;

        // 工具结果 Diff
        public bool DiffEnabled = true;
        public double DiffThreshold = 0.30;

        // MCP 服务地址
        public string GameMcpHost = "localhost";
        public int GameMcpPort = 9877;
        public int AgentMcpPort = 9878;

        // Agent 行为
        public bool AgentAutoRun = true;
        // PlanSpeed 已移除 — Plan/Act 均强制暂停，仅 Advance 可推进
        public string SkillsDir = "";
        public string ProjectPath = "";

        // ACP Backend / Node.js
        public string SelectedAcpBackendId = "";
        public string NodeExecutablePath = "";
        public bool LogAcpIpc;
        public List<AcpBackendSetting> AcpBackends = new List<AcpBackendSetting>();

        // UIMessageBus（Web 前端 WS 服务）
        public string BridgeHost = "127.0.0.1";
        public int BridgePort = 19999;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref TokenBudgetLimit, "tokenBudgetLimit", 0L);
            Scribe_Values.Look(ref DiffEnabled, "diffEnabled", true);
            Scribe_Values.Look(ref DiffThreshold, "diffThreshold", 0.30);
            Scribe_Values.Look(ref GameMcpHost, "gameMcpHost", "localhost");
            Scribe_Values.Look(ref GameMcpPort, "gameMcpPort", 9877);
            Scribe_Values.Look(ref AgentMcpPort, "agentMcpPort", 9878);
            Scribe_Values.Look(ref AgentAutoRun, "agentAutoRun", true);
            Scribe_Values.Look(ref SkillsDir, "skillsDir", "");
            Scribe_Values.Look(ref ProjectPath, "projectPath", "");
            Scribe_Values.Look(ref SelectedAcpBackendId, "selectedAcpBackendId", "");
            Scribe_Values.Look(ref NodeExecutablePath, "nodeExecutablePath", "");
            Scribe_Values.Look(ref LogAcpIpc, "logAcpIpc", false);
            Scribe_Collections.Look(ref AcpBackends, "acpBackends", LookMode.Deep);
            EnsureAcpBackendDefaults();
            Scribe_Values.Look(ref BridgeHost, "bridgeHost", "127.0.0.1");
            Scribe_Values.Look(ref BridgePort, "bridgePort", 19999);
        }

        public void EnsureAcpBackendDefaults()
        {
            if (AcpBackends == null) AcpBackends = new List<AcpBackendSetting>();
            // 旧版本会自动写入 bundled Claude；迁移时移除，所有 backend 都必须由用户显式配置。
            AcpBackends.RemoveAll(backend => backend == null || backend.IsBundled);
            if (string.IsNullOrWhiteSpace(SelectedAcpBackendId)
                || !AcpBackends.Exists(backend => backend != null && backend.Enabled && backend.Id == SelectedAcpBackendId))
            {
                var firstEnabled = AcpBackends.Find(backend => backend != null && backend.Enabled);
                SelectedAcpBackendId = firstEnabled?.Id ?? "";
            }
        }
    }
}
