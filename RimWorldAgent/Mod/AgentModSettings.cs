using System.Collections.Generic;
using Verse;

namespace RimWorldAgent
{
    public class CustomMcpServerSetting : IExposable
    {
        public bool Enabled = true;
        public string Name = "";
        public string Type = "http";
        public string Url = "";
        public string Command = "npx";
        public string ArgsText = "";
        public string EnvText = "";
        public int Timeout = 300000;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "enabled", true);
            Scribe_Values.Look(ref Name, "name", "");
            Scribe_Values.Look(ref Type, "type", "http");
            Scribe_Values.Look(ref Url, "url", "");
            Scribe_Values.Look(ref Command, "command", "npx");
            Scribe_Values.Look(ref ArgsText, "argsText", "");
            Scribe_Values.Look(ref EnvText, "envText", "");
            Scribe_Values.Look(ref Timeout, "timeout", 300000);
        }
    }

    public class AgentModSettings : ModSettings
    {
        // 模型
        public string ModelName = "";

        // 思考
        public string ThinkingMode = "adaptive";
        public string ThinkingEffort = "high";

        // Token 预算
        public long TokenBudgetLimit;
        public string TokenBudgetAction = "Block";

        // MCP 服务地址
        public string GameMcpHost = "localhost";
        public int GameMcpPort = 9877;
        public int AgentMcpPort = 9878;
        public List<CustomMcpServerSetting> CustomMcpServers = new List<CustomMcpServerSetting>();

        // Agent 行为
        public bool AgentAutoRun = true;
        public string PlanSpeed = "paused";
        public string SkillsDir = "";
        public string ProjectPath = "";

        // API 配置（写入 ProjectPath/.claude/settings.local.json）
        public string ApiKey = "";
        public string ApiUrl = "";

        // CC Companion 依赖
        public bool CcbAutoInstall = true;

        // UIMessageBus（Web 前端 WS 服务）
        public string BridgeHost = "127.0.0.1";
        public int BridgePort = 19999;

        // 日志
        public bool LogSdkMessages;
        public bool LogCcbWsMessages;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ModelName, "modelName", "");
            Scribe_Values.Look(ref ThinkingMode, "thinkingMode", "adaptive");
            Scribe_Values.Look(ref ThinkingEffort, "thinkingEffort", "high");
            Scribe_Values.Look(ref TokenBudgetLimit, "tokenBudgetLimit", 0L);
            Scribe_Values.Look(ref TokenBudgetAction, "tokenBudgetAction", "Block");
            Scribe_Values.Look(ref GameMcpHost, "gameMcpHost", "localhost");
            Scribe_Values.Look(ref GameMcpPort, "gameMcpPort", 9877);
            Scribe_Values.Look(ref AgentMcpPort, "agentMcpPort", 9878);
            Scribe_Collections.Look(ref CustomMcpServers, "customMcpServers", LookMode.Deep);
            if (CustomMcpServers == null) CustomMcpServers = new List<CustomMcpServerSetting>();
            Scribe_Values.Look(ref AgentAutoRun, "agentAutoRun", true);
            Scribe_Values.Look(ref PlanSpeed, "planSpeed", "paused");
            Scribe_Values.Look(ref SkillsDir, "skillsDir", "");
            Scribe_Values.Look(ref ProjectPath, "projectPath", "");
            Scribe_Values.Look(ref ApiKey, "apiKey", "");
            Scribe_Values.Look(ref ApiUrl, "apiUrl", "");
            Scribe_Values.Look(ref CcbAutoInstall, "ccbAutoInstall", true);
            Scribe_Values.Look(ref BridgeHost, "bridgeHost", "127.0.0.1");
            Scribe_Values.Look(ref BridgePort, "bridgePort", 19999);
            Scribe_Values.Look(ref LogSdkMessages, "logSdkMessages", false);
            Scribe_Values.Look(ref LogCcbWsMessages, "logCcbWsMessages", false);
        }
    }
}
