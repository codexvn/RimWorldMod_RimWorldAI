using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent
{
    public enum BudgetStatus { Ok, Warning, Critical, Exceeded }

    /// <summary>Token 预算服务 — 预算检查 + 格式化。数据存储委托给 Core.Data.TokenStore。</summary>
    public static class TokenUsageTracker
    {
        /// <summary>Record() 完成后触发，通知外部（如 CCB 推送预算更新）</summary>
        public static event Action? OnUsageRecorded;

        // ===== 属性委托到 TokenStore =====

        public static string CurrentModel
        {
            get => TokenStore.CurrentModel;
            set => TokenStore.CurrentModel = value;
        }

        public static long TotalInputTokens => TokenStore.TotalInputTokens;
        public static long TotalOutputTokens => TokenStore.TotalOutputTokens;
        public static long TotalCacheReadTokens => TokenStore.TotalCacheReadTokens;
        public static long TotalCacheCreateTokens => TokenStore.TotalCacheCreateTokens;
        public static long TotalAllTokens => TokenStore.TotalAllTokens;
        public static int TotalRequests => TokenStore.TotalRequests;
        public static int TotalToolSuccess => TokenStore.TotalToolSuccess;
        public static int TotalToolFailure => TokenStore.TotalToolFailure;
        public static long TotalDurationMs => TokenStore.TotalDurationMs;

        // ===== 记录（委托 TokenStore + 全局持久化 + 事件） =====

        public static void Record(string model, long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            TokenStore.Record(model, inputTokens, outputTokens, cacheRead, cacheCreate, durationMs);

            var key = string.IsNullOrEmpty(model) ? "unknown" : model;
            GlobalModelUsageStore.Contribute(key, inputTokens, outputTokens, cacheRead, cacheCreate);

            try { OnUsageRecorded?.Invoke(); }
            catch (Exception ex) { CoreLog.Warn($"[TokenUsage] 推送预算更新失败: {ex.Message}"); }
        }

        /// <summary>兼容旧的无模型名调用</summary>
        public static void Record(long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            Record(CurrentModel, inputTokens, outputTokens, cacheRead, cacheCreate, durationMs);
        }

        public static void RecordToolResult(bool isError)
        {
            TokenStore.RecordToolResult(isError);
        }

        // ===== 预算检查 =====

        private const double WarningThreshold = 0.80;
        private const double CriticalThreshold = 0.95;

        public static BudgetStatus CheckBudget(long limit)
        {
            if (limit <= 0) return BudgetStatus.Ok;
            long total = TotalAllTokens;
            if (total >= limit) return BudgetStatus.Exceeded;
            double pct = (double)total / limit;
            if (pct >= CriticalThreshold) return BudgetStatus.Critical;
            if (pct >= WarningThreshold) return BudgetStatus.Warning;
            return BudgetStatus.Ok;
        }

        public static double GetBudgetUsagePercent(long limit)
        {
            if (limit <= 0) return 0;
            return (double)TotalAllTokens / limit * 100.0;
        }

        // ===== 格式化输出 =====

        public static string GetSummary()
        {
            if (TotalRequests == 0)
                return "暂无 Token 消耗记录";

            var sb = new StringBuilder();
            long totalTokens = TotalInputTokens + TotalOutputTokens;
            double avgDurationSec = TotalDurationMs / (double)TotalRequests / 1000.0;
            long totalInputWithCache = TotalInputTokens + TotalCacheReadTokens;
            double cacheHitRate = totalInputWithCache > 0
                ? (double)TotalCacheReadTokens / totalInputWithCache * 100.0
                : 0.0;

            sb.AppendLine("## Token 消耗统计");
            sb.AppendLine($"- 累计请求: {TotalRequests} 次 | 总耗时: {TotalDurationMs / 1000.0:F0} 秒 (均 {avgDurationSec:F1}s/次)");
            sb.AppendLine($"- 输入 Token: {TotalInputTokens:N0} | 缓存命中: {TotalCacheReadTokens:N0} ({cacheHitRate:F1}%) | 缓存新建: {TotalCacheCreateTokens:N0}");
            sb.AppendLine($"- 输出 Token: {TotalOutputTokens:N0}");
            sb.AppendLine($"- 合计 Token: {totalTokens:N0}");
            sb.AppendLine($"- 工具调用: {TotalToolSuccess + TotalToolFailure} 次 (成功 {TotalToolSuccess}, 失败 {TotalToolFailure})");

            var modelUsages = TokenStore.GetModelUsages();
            if (modelUsages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("### 按模型");
                foreach (var kv in modelUsages.OrderByDescending(kv => kv.Value.TotalTokens))
                {
                    var d = kv.Value;
                    sb.AppendLine($"- **{kv.Key}**: 合计 {d.TotalTokens:N0} | 入 {d.InputTokens:N0} | 出 {d.OutputTokens:N0} | 缓存 {d.CacheReadTokens:N0} | {d.RequestCount} 次");
                }
            }

            return sb.ToString();
        }

        /// <summary>紧凑格式供游戏内 UI 底栏，含预算进度条</summary>
        public static string GetCompactDisplay(long budgetLimit = 0)
        {
            if (TotalRequests == 0) return "Token: --";

            string fmt(long v) => v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" :
                                  v >= 1_000 ? $"{v / 1_000f:F0}K" : v.ToString();

            long totalTokens = TotalInputTokens + TotalOutputTokens;
            long totalInputWithCache = TotalInputTokens + TotalCacheReadTokens;
            double cacheHitRate = totalInputWithCache > 0
                ? (double)TotalCacheReadTokens / totalInputWithCache * 100.0
                : 0.0;

            int totalCalls = TotalToolSuccess + TotalToolFailure;
            string toolStr = totalCalls > 0
                ? $"工具 {TotalToolSuccess}✓{(TotalToolFailure > 0 ? $" {TotalToolFailure}✗" : "")} | "
                : "";

            string tokenPart;
            if (budgetLimit > 0)
            {
                double pct = (double)TotalAllTokens / budgetLimit * 100.0;
                int blocks = (int)(pct / 10.0);
                if (blocks > 10) blocks = 10;
                string bar = new string('█', blocks) + new string('░', 10 - blocks);
                tokenPart = $"Token: {fmt(TotalAllTokens)}/{fmt(budgetLimit)} ({pct:F0}%) {bar}";
            }
            else
            {
                tokenPart = $"Token: {fmt(totalTokens)}";
            }

            return $"{tokenPart} | 缓存 {fmt(TotalCacheReadTokens)}({cacheHitRate:F0}%) | {toolStr}{TotalRequests}轮";
        }
    }
}
