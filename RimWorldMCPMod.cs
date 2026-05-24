using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    public class RimWorldMCPMod : Mod
    {
        public static RimWorldMCPMod Instance { get; private set; } = null!;
        public McpModSettings Settings { get; private set; }

        public RimWorldMCPMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<McpModSettings>();
        }

        public override string SettingsCategory()
        {
            return "RimWorld MCP";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled("启用 OSS 上传", ref Settings.OssEnabled,
                "开启后，截图将自动上传到 S3 兼容的对象存储（如阿里云 OSS、MinIO 等）");

            if (Settings.OssEnabled)
            {
                listing.Gap(12f);

                listing.Label("Service URL");
                Settings.OssServiceUrl = listing.TextEntry(Settings.OssServiceUrl);

                listing.Label("Bucket 名称");
                Settings.OssBucketName = listing.TextEntry(Settings.OssBucketName);

                listing.Label("Access Key");
                Settings.OssAccessKey = listing.TextEntry(Settings.OssAccessKey);

                listing.Label("Secret Key");
                Settings.OssSecretKey = listing.TextEntry(Settings.OssSecretKey);

                listing.Label("Region");
                Settings.OssRegion = listing.TextEntry(Settings.OssRegion);

                listing.Gap(12f);
                listing.CheckboxLabeled("Force Path Style", ref Settings.OssForcePathStyle,
                    "勾选 = endpoint/bucket/key，不勾选 = bucket.endpoint/key");

                listing.Gap(12f);
                listing.CheckboxLabeled("使用签名 URL", ref Settings.OssUseSignedUrl,
                    "生成有时效的预签名 URL，Bucket 无需设为公开读。关闭则返回公开 URL。");

                if (Settings.OssUseSignedUrl)
                {
                    listing.Label("签名有效期（小时）");
                    var expiryStr = listing.TextEntry(Settings.OssSignedUrlExpiryHours.ToString());
                    if (int.TryParse(expiryStr, out int expiryHours) && expiryHours > 0)
                        Settings.OssSignedUrlExpiryHours = expiryHours;
                }
            }

            listing.End();
        }
    }
}
