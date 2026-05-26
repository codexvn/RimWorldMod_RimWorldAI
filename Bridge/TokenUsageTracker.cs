using System.Text;
using System.Threading;
using Verse;

namespace RimWorldMCP
{
    /// <summary>Token 消耗追踪器 — 从 CC Companion 接收每次 API 调用的用量，累积并持久化到存档</summary>
    public static class TokenUsageTracker
    {
        public static long TotalInputTokens;
        public static long TotalOutputTokens;
        public static long TotalCacheReadTokens;
        public static long TotalCacheCreateTokens;
        public static int TotalRequests;
        public static int TotalToolSuccess;
        public static int TotalToolFailure;
        public static long TotalDurationMs;

        public static void Record(long inputTokens, long outputTokens, long cacheRead, long cacheCreate, long durationMs)
        {
            Interlocked.Add(ref TotalInputTokens, inputTokens);
            Interlocked.Add(ref TotalOutputTokens, outputTokens);
            Interlocked.Add(ref TotalCacheReadTokens, cacheRead);
            Interlocked.Add(ref TotalCacheCreateTokens, cacheCreate);
            Interlocked.Increment(ref TotalRequests);
            Interlocked.Add(ref TotalDurationMs, durationMs);
        }

        public static void RecordToolResult(bool isError)
        {
            if (isError)
                Interlocked.Increment(ref TotalToolFailure);
            else
                Interlocked.Increment(ref TotalToolSuccess);
        }

        public static void ExposeData()
        {
            Scribe_Values.Look(ref TotalInputTokens, "usageInputTokens", 0L);
            Scribe_Values.Look(ref TotalOutputTokens, "usageOutputTokens", 0L);
            Scribe_Values.Look(ref TotalCacheReadTokens, "usageCacheRead", 0L);
            Scribe_Values.Look(ref TotalCacheCreateTokens, "usageCacheCreate", 0L);
            Scribe_Values.Look(ref TotalRequests, "usageRequests", 0);
            Scribe_Values.Look(ref TotalToolSuccess, "usageToolSuccess", 0);
            Scribe_Values.Look(ref TotalToolFailure, "usageToolFailure", 0);
            Scribe_Values.Look(ref TotalDurationMs, "usageDurationMs", 0L);
        }

        public static string GetSummary()
        {
            if (TotalRequests == 0)
                return "暂无 Token 消耗记录";

            var sb = new StringBuilder();
            long totalTokens = TotalInputTokens + TotalOutputTokens;
            double avgDurationSec = TotalDurationMs / (double)TotalRequests / 1000.0;
            double cacheHitRate = TotalInputTokens > 0
                ? (double)TotalCacheReadTokens / TotalInputTokens * 100.0
                : 0.0;

            sb.AppendLine($"## Token 消耗统计");
            sb.AppendLine($"- 累计请求: {TotalRequests} 次 | 总耗时: {TotalDurationMs / 1000.0:F0} 秒 (均 {avgDurationSec:F1}s/次)");
            sb.AppendLine($"- 输入 Token: {TotalInputTokens:N0} | 缓存命中: {TotalCacheReadTokens:N0} ({cacheHitRate:F1}%) | 缓存新建: {TotalCacheCreateTokens:N0}");
            sb.AppendLine($"- 输出 Token: {TotalOutputTokens:N0}");
            sb.AppendLine($"- 合计 Token: {totalTokens:N0}");
            sb.AppendLine($"- 工具调用: {TotalToolSuccess + TotalToolFailure} 次 (成功 {TotalToolSuccess}, 失败 {TotalToolFailure})");

            return sb.ToString();
        }

        /// <summary>紧凑格式供游戏内 UI 底栏使用</summary>
        public static string GetCompactDisplay()
        {
            if (TotalRequests == 0) return "Token: --";
            long totalTokens = TotalInputTokens + TotalOutputTokens;
            double cacheHitRate = TotalInputTokens > 0
                ? (double)TotalCacheReadTokens / TotalInputTokens * 100.0
                : 0.0;

            string fmt(long v) => v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" :
                                  v >= 1_000 ? $"{v / 1_000f:F0}K" : v.ToString();

            int totalCalls = TotalToolSuccess + TotalToolFailure;
            string toolStr = totalCalls > 0
                ? $"工具 {TotalToolSuccess}✓{(TotalToolFailure > 0 ? $" {TotalToolFailure}✗" : "")} | "
                : "";
            return $"Token: {fmt(totalTokens)} | 缓存 {fmt(TotalCacheReadTokens)}({cacheHitRate:F0}%) | {toolStr}{TotalRequests}轮";
        }
    }
}
