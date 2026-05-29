namespace RimWorldMCP
{
    public static class McpOssConfig
    {
        public static bool Enabled { get; set; }
        public static string ServiceUrl { get; set; } = "";
        public static string BucketName { get; set; } = "";
        public static string AccessKey { get; set; } = "";
        public static string SecretKey { get; set; } = "";
        public static bool UseSignedUrl { get; set; } = true;
        public static int SignedUrlExpiryHours { get; set; } = 24;

        public static bool IsConfigured =>
            Enabled &&
            !string.IsNullOrEmpty(ServiceUrl) &&
            !string.IsNullOrEmpty(BucketName) &&
            !string.IsNullOrEmpty(AccessKey) &&
            !string.IsNullOrEmpty(SecretKey);

        public static void LoadFromModSettings(McpModSettings settings)
        {
            if (settings == null) return;

            Enabled = settings.OssEnabled;
            ServiceUrl = settings.OssServiceUrl ?? "";
            BucketName = settings.OssBucketName ?? "";
            AccessKey = settings.OssAccessKey ?? "";
            SecretKey = settings.OssSecretKey ?? "";
            UseSignedUrl = settings.OssUseSignedUrl;
            SignedUrlExpiryHours = settings.OssSignedUrlExpiryHours;

            McpLog.Info(IsConfigured
                ? $"OSS 配置已加载: {ServiceUrl}/{BucketName}"
                : "OSS 未配置或未启用");
        }
    }
}
