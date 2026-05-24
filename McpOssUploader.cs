using System;
using System.Collections.Concurrent;
using System.IO;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace RimWorldMCP
{
    public static class McpOssUploader
    {
        private const double MinWaitSeconds = 2.0;  // 至少等 Unity 写完文件
        private const double MaxWaitSeconds = 60.0; // 最多等 60 秒

        private static readonly ConcurrentQueue<(string filePath, string objectKey, DateTime enqueuedAt)> _pending = new();

        public static void EnqueuePendingUpload(string filePath, string objectKey)
        {
            _pending.Enqueue((Path.GetFullPath(filePath), objectKey, DateTime.UtcNow));
        }

        /// <summary>主线程每帧调用，时间基准等待文件就绪后再上传</summary>
        public static void ProcessPendingUploads()
        {
            if (!McpOssConfig.IsConfigured || _pending.IsEmpty) return;

            var toRetry = new System.Collections.Generic.List<(string, string, DateTime)>();

            while (_pending.TryDequeue(out var item))
            {
                double elapsed = (DateTime.UtcNow - item.enqueuedAt).TotalSeconds;

                try
                {
                    if (elapsed < MinWaitSeconds)
                    {
                        toRetry.Add(item); // 还没到最短等待时间，下帧再看
                        continue;
                    }

                    if (!IsFileReady(item.filePath))
                    {
                        if (elapsed < MaxWaitSeconds)
                        {
                            toRetry.Add(item); // 文件尚未就绪，继续等
                        }
                        else
                        {
                            McpLog.Warn($"OSS 上传放弃（等待 {MaxWaitSeconds:F0} 秒后文件仍未就绪）: {item.filePath}");
                        }
                        continue;
                    }

                    UploadInternal(item.filePath, item.objectKey);
                }
                catch (Exception ex)
                {
                    McpLog.Error($"OSS 上传失败 ({item.objectKey}): {ex.Message}");
                }
            }

            foreach (var retry in toRetry)
                _pending.Enqueue(retry);
        }

        /// <summary>尝试打开文件读取，确认写入已完毕</summary>
        private static bool IsFileReady(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (File.OpenRead(path)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void UploadInternal(string filePath, string objectKey)
        {
            var s3Config = new AmazonS3Config
            {
                ServiceURL = McpOssConfig.NormalizeUrl(McpOssConfig.ServiceUrl),
                ForcePathStyle = McpOssConfig.ForcePathStyle,
                AuthenticationRegion = McpOssConfig.Region,
            };

            using var client = new AmazonS3Client(McpOssConfig.AccessKey, McpOssConfig.SecretKey, s3Config);
            using var fileStream = new FileStream(Path.GetFullPath(filePath), FileMode.Open, FileAccess.Read, FileShare.Read);
            client.PutObject(new PutObjectRequest
            {
                BucketName = McpOssConfig.BucketName,
                Key = objectKey,
                InputStream = fileStream,
                ContentType = "image/png",
                DisablePayloadSigning = true
            });

            McpLog.Info($"OSS 上传成功: {objectKey}");

            try { File.Delete(filePath); }
            catch (Exception ex) { McpLog.Warn($"删除临时截图失败: {ex.Message}"); }
        }

        public static string GetPublicUrl(string objectKey)
        {
            if (McpOssConfig.UseSignedUrl)
                return GetSignedUrl(objectKey);

            if (McpOssConfig.ForcePathStyle)
                return $"{McpOssConfig.ServiceUrl}/{McpOssConfig.BucketName}/{objectKey}";

            try
            {
                return $"https://{McpOssConfig.BucketName}.{new Uri(McpOssConfig.ServiceUrl).Host}/{objectKey}";
            }
            catch (UriFormatException)
            {
                return $"{McpOssConfig.ServiceUrl}/{McpOssConfig.BucketName}/{objectKey}";
            }
        }

        private static string GetSignedUrl(string objectKey)
        {
            var s3Config = new AmazonS3Config
            {
                ServiceURL = McpOssConfig.NormalizeUrl(McpOssConfig.ServiceUrl),
                ForcePathStyle = McpOssConfig.ForcePathStyle,
                AuthenticationRegion = McpOssConfig.Region
            };

            using var client = new AmazonS3Client(McpOssConfig.AccessKey, McpOssConfig.SecretKey, s3Config);
            return client.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = McpOssConfig.BucketName,
                Key = objectKey,
                Expires = DateTime.UtcNow.AddHours(McpOssConfig.SignedUrlExpiryHours),
                Verb = HttpVerb.GET,
                Protocol = Protocol.HTTPS
            });
        }
    }
}
