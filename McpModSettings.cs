using Verse;

namespace RimWorldMCP
{
    public class McpModSettings : ModSettings
    {
        public bool OssEnabled = false;
        public string OssServiceUrl = "";
        public string OssBucketName = "";
        public string OssAccessKey = "";
        public string OssSecretKey = "";
        public string OssRegion = "";
        public bool OssForcePathStyle = false;
        public bool OssUseSignedUrl = true;
        public int OssSignedUrlExpiryHours = 24;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref OssEnabled, "ossEnabled", false);
            Scribe_Values.Look(ref OssServiceUrl, "ossServiceUrl", "");
            Scribe_Values.Look(ref OssBucketName, "ossBucketName", "");
            Scribe_Values.Look(ref OssAccessKey, "ossAccessKey", "");
            Scribe_Values.Look(ref OssSecretKey, "ossSecretKey", "");
            Scribe_Values.Look(ref OssRegion, "ossRegion", "");
            Scribe_Values.Look(ref OssForcePathStyle, "ossForcePathStyle", false);
            Scribe_Values.Look(ref OssUseSignedUrl, "ossUseSignedUrl", true);
            Scribe_Values.Look(ref OssSignedUrlExpiryHours, "ossSignedUrlExpiryHours", 24);
        }
    }
}
