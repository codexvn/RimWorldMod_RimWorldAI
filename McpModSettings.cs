using Verse;

namespace RimWorldMCP
{
    public class McpModSettings : ModSettings
    {
        // 桥接器
        public int BridgeType = 0; // 0=无, 1=OpenClaw
        public string BridgeUrl = "";
        public string BridgeToken = "";
        public string BridgePassword = "";

        // OSS
        public bool OssEnabled = false;
        public string OssServiceUrl = "";
        public string OssBucketName = "";
        public string OssAccessKey = "";
        public string OssSecretKey = "";
        public bool OssUseSignedUrl = true;
        public int OssSignedUrlExpiryHours = 24;

        public static readonly string[] BridgeTypeLabels = { "无", "OpenClaw" };

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref BridgeType, "bridgeType", 0);
            Scribe_Values.Look(ref BridgeUrl, "bridgeUrl", "");
            Scribe_Values.Look(ref BridgeToken, "bridgeToken", "");
            Scribe_Values.Look(ref BridgePassword, "bridgePassword", "");
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
