using Verse;

namespace RimWorldMCP
{
    public enum CompressionMethod { Uncompressed, RLE, RowRefRLE }

    public class McpModSettings : ModSettings
    {
        // 调试
        public LogLevel LogLevel = LogLevel.Info;

        // MCP 服务器
        public string McpHost = "0.0.0.0";
        public int McpPort = 9877;

        // 工具行为
        public bool AutoMoveCamera = true;
        public bool AutoTrackColonists = true;
        public bool AutoObserveOverlay = true;

        // OSS
        public bool OssEnabled;
        public string OssServiceUrl = "";
        public string OssBucketName = "";
        public string OssAccessKey = "";
        public string OssSecretKey = "";
        public bool OssUseSignedUrl = true;
        public int OssSignedUrlExpiryHours = 24;

        // 地图分块与压缩
        public int ChunkWidth = 32;
        public int ChunkHeight = 32;
        public CompressionMethod GridCompression = CompressionMethod.RLE;

        public static readonly string[] LogLevelLabels = { "Debug", "Info", "Warn", "Error" };
        public static readonly string[] CompressionMethodLabels = { "未压缩", "RLE", "行引用+RLE" };

        public override void ExposeData()
        {
            base.ExposeData();
            var logLevelInt = (int)LogLevel;
            Scribe_Values.Look(ref McpHost, "mcpHost", "0.0.0.0");
            Scribe_Values.Look(ref McpPort, "mcpPort", 9877);
            Scribe_Values.Look(ref logLevelInt, "logLevel", (int)LogLevel.Info);
            LogLevel = (LogLevel)logLevelInt;
            Scribe_Values.Look(ref AutoMoveCamera, "autoMoveCamera", true);
            Scribe_Values.Look(ref AutoTrackColonists, "autoTrackColonists", true);
            Scribe_Values.Look(ref AutoObserveOverlay, "autoObserveOverlay", true);
            Scribe_Values.Look(ref OssEnabled, "ossEnabled", false);
            Scribe_Values.Look(ref OssServiceUrl, "ossServiceUrl", "");
            Scribe_Values.Look(ref OssBucketName, "ossBucketName", "");
            Scribe_Values.Look(ref OssAccessKey, "ossAccessKey", "");
            Scribe_Values.Look(ref OssSecretKey, "ossSecretKey", "");
            Scribe_Values.Look(ref OssUseSignedUrl, "ossUseSignedUrl", true);
            Scribe_Values.Look(ref OssSignedUrlExpiryHours, "ossSignedUrlExpiryHours", 24);
            var gridCompression = (int)GridCompression;
            Scribe_Values.Look(ref ChunkWidth, "chunkWidth", 32);
            Scribe_Values.Look(ref ChunkHeight, "chunkHeight", 32);
            Scribe_Values.Look(ref gridCompression, "gridCompression", (int)CompressionMethod.RLE);
            GridCompression = (CompressionMethod)gridCompression;
        }
    }
}
