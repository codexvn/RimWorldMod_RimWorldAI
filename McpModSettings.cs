using Verse;

namespace RimWorldMCP
{
    public class McpModSettings : ModSettings
    {
        // 调试
        public LogLevel LogLevel = LogLevel.Info;

        // MCP 服务器
        public string McpHost = "0.0.0.0";
        public int McpPort = 9877;

        // CC 桥接
        public string CCUrl = "ws://127.0.0.1:19999";
        public int LocalCCPort = 19999;
        public string CCToken = "";
        public bool CCAutoStart = true;

        // OSS
        public bool OssEnabled = false;
        public string OssServiceUrl = "";
        public string OssBucketName = "";
        public string OssAccessKey = "";
        public string OssSecretKey = "";
        public bool OssUseSignedUrl = true;
        public int OssSignedUrlExpiryHours = 24;

        public static readonly string[] LogLevelLabels = { "Debug", "Info", "Warn", "Error" };

        public override void ExposeData()
        {
            base.ExposeData();
            var logLevelInt = (int)LogLevel;
            Scribe_Values.Look(ref McpHost, "mcpHost", "0.0.0.0");
            Scribe_Values.Look(ref McpPort, "mcpPort", 9877);
            Scribe_Values.Look(ref logLevelInt, "logLevel", (int)LogLevel.Info);
            LogLevel = (LogLevel)logLevelInt;
            Scribe_Values.Look(ref CCUrl, "ccUrl", "ws://127.0.0.1:19999");
            Scribe_Values.Look(ref LocalCCPort, "localCCPort", 19999);
            Scribe_Values.Look(ref CCToken, "ccToken", "");
            Scribe_Values.Look(ref CCAutoStart, "ccAutoStart", true);
            Scribe_Values.Look(ref OssEnabled, "ossEnabled", false);
            Scribe_Values.Look(ref OssServiceUrl, "ossServiceUrl", "");
            Scribe_Values.Look(ref OssBucketName, "ossBucketName", "");
            Scribe_Values.Look(ref OssAccessKey, "ossAccessKey", "");
            Scribe_Values.Look(ref OssSecretKey, "ossSecretKey", "");
            Scribe_Values.Look(ref OssUseSignedUrl, "ossUseSignedUrl", true);
            Scribe_Values.Look(ref OssSignedUrlExpiryHours, "ossSignedUrlExpiryHours", 24);
        }
    }
}
