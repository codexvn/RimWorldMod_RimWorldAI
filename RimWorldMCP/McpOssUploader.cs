using System;
using System.Collections.Concurrent;
using System.IO;
using Aliyun.OSS;

namespace RimWorldMCP
{
    public static class McpOssUploader
    {
        private const double MinWaitSeconds = 2.0;
        private const double MaxWaitSeconds = 60.0;

        private static readonly ConcurrentQueue<(string filePath, string objectKey, DateTime enqueuedAt)> _pending = new();

        public static void EnqueuePendingUpload(string filePath, string objectKey)
        {
            _pending.Enqueue((Path.GetFullPath(filePath), objectKey, DateTime.UtcNow));
        }

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
                        toRetry.Add(item);
                        continue;
                    }

                    if (!IsFileReady(item.filePath))
                    {
                        if (elapsed < MaxWaitSeconds)
                        {
                            toRetry.Add(item);
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
            var client = new OssClient(McpOssConfig.ServiceUrl, McpOssConfig.AccessKey, McpOssConfig.SecretKey);
            var metadata = new ObjectMetadata { ContentType = "image/png" };
            client.PutObject(McpOssConfig.BucketName, objectKey, Path.GetFullPath(filePath), metadata);

            McpLog.Info($"OSS 上传成功: {objectKey}");

            try { File.Delete(filePath); }
            catch (Exception ex) { McpLog.Warn($"删除临时截图失败: {ex.Message}"); }
        }

        public static string GetPublicUrl(string objectKey)
        {
            if (McpOssConfig.UseSignedUrl)
                return GetSignedUrl(objectKey);

            return $"{McpOssConfig.ServiceUrl}/{McpOssConfig.BucketName}/{objectKey}";
        }

        private static string GetSignedUrl(string objectKey)
        {
            var client = new OssClient(McpOssConfig.ServiceUrl, McpOssConfig.AccessKey, McpOssConfig.SecretKey);
            var expiry = DateTime.UtcNow.AddHours(McpOssConfig.SignedUrlExpiryHours);
            var uri = client.GeneratePresignedUri(McpOssConfig.BucketName, objectKey, expiry, SignHttpMethod.Get);
            return uri.ToString();
        }
    }
}
