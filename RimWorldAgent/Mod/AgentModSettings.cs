using Verse;

namespace RimWorldAgent
{
    public class AgentModSettings : ModSettings
    {
        // CCB 桥接
        public int CCBPort = 19999;
        public bool CCBAutoStart = true;
        public bool CCBAutoInstall = true;
        public string CCBHost = "0.0.0.0";
        public string CCBRemoteHost = "127.0.0.1";
        public int CCBRemotePort = 19999;
        public string CCBAuthToken = "";
        public string CCBModelName = "";
        public int CCBMaxThinkingTokens;
        public string CCBThinkingEffort = "medium";

        // Token 预算
        public long TokenBudgetLimit;

        // MCP 服务端口
        public int McpPort = 9877;
        public int AgentMcpPort = 9878;

        // Agent 行为
        public int LoopIntervalMs = 10000;
        public bool AgentAutoRun = true;
        public string PlanSpeed = "paused";
        public string SkillsDir = "";
        public string SessionDir = "";

        public static readonly string[] LogLevelLabels = { "Debug", "Info", "Warn", "Error" };

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref CCBPort, "ccbPort", 19999);
            Scribe_Values.Look(ref CCBAutoStart, "ccbAutoStart", true);
            Scribe_Values.Look(ref CCBAutoInstall, "ccbAutoInstall", true);
            Scribe_Values.Look(ref CCBHost, "ccbHost", "0.0.0.0");
            Scribe_Values.Look(ref CCBRemoteHost, "ccbRemoteHost", "127.0.0.1");
            Scribe_Values.Look(ref CCBRemotePort, "ccbRemotePort", 19999);
            Scribe_Values.Look(ref CCBAuthToken, "ccbAuthToken", "");
            Scribe_Values.Look(ref CCBModelName, "ccbModelName", "");
            Scribe_Values.Look(ref CCBMaxThinkingTokens, "ccbMaxThinkingTokens", 0);
            Scribe_Values.Look(ref CCBThinkingEffort, "ccbThinkingEffort", "medium");
            Scribe_Values.Look(ref TokenBudgetLimit, "tokenBudgetLimit", 0L);
            Scribe_Values.Look(ref McpPort, "mcpPort", 9877);
            Scribe_Values.Look(ref AgentMcpPort, "agentMcpPort", 9878);
            Scribe_Values.Look(ref LoopIntervalMs, "loopIntervalMs", 10000);
            Scribe_Values.Look(ref AgentAutoRun, "agentAutoRun", true);
            Scribe_Values.Look(ref PlanSpeed, "planSpeed", "paused");
            Scribe_Values.Look(ref SkillsDir, "skillsDir", "");
            Scribe_Values.Look(ref SessionDir, "sessionDir", "");
        }
    }
}
