using Verse;

namespace RimWorldAgent
{
    public class AgentModSettings : ModSettings
    {
        // 模型
        public string ModelName = "";

        // 思考
        public string ThinkingMode = "default";
        public string ThinkingEffort = "medium";
        public int MaxThinkingTokens;

        // Token 预算
        public long TokenBudgetLimit;
        public string TokenBudgetAction = "Block";

        // MCP 服务地址
        public string GameMcpHost = "localhost";
        public int GameMcpPort = 9877;
        public int AgentMcpPort = 9878;

        // Agent 行为
        public bool AgentAutoRun = true;
        public string PlanSpeed = "paused";
        public string SkillsDir = "";
        public string ProjectPath = "";

        // CC Companion 依赖
        public bool CcbAutoInstall = true;

        // UIMessageBus（Web 前端 WS 服务）
        public string BridgeHost = "127.0.0.1";
        public int BridgePort = 19999;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ModelName, "modelName", "");
            Scribe_Values.Look(ref ThinkingMode, "thinkingMode", "default");
            Scribe_Values.Look(ref ThinkingEffort, "thinkingEffort", "medium");
            Scribe_Values.Look(ref MaxThinkingTokens, "maxThinkingTokens", 0);
            Scribe_Values.Look(ref TokenBudgetLimit, "tokenBudgetLimit", 0L);
            Scribe_Values.Look(ref TokenBudgetAction, "tokenBudgetAction", "Block");
            Scribe_Values.Look(ref GameMcpHost, "gameMcpHost", "localhost");
            Scribe_Values.Look(ref GameMcpPort, "gameMcpPort", 9877);
            Scribe_Values.Look(ref AgentMcpPort, "agentMcpPort", 9878);
            Scribe_Values.Look(ref AgentAutoRun, "agentAutoRun", true);
            Scribe_Values.Look(ref PlanSpeed, "planSpeed", "paused");
            Scribe_Values.Look(ref SkillsDir, "skillsDir", "");
            Scribe_Values.Look(ref ProjectPath, "projectPath", "");
            Scribe_Values.Look(ref CcbAutoInstall, "ccbAutoInstall", true);
            Scribe_Values.Look(ref BridgeHost, "bridgeHost", "127.0.0.1");
            Scribe_Values.Look(ref BridgePort, "bridgePort", 19999);
        }
    }
}
