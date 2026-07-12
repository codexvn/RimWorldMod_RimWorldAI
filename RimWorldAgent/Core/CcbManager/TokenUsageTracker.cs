using System;
using System.Linq;
using System.Text;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent
{
    public enum BudgetStatus { Ok, Warning, Critical, Exceeded }

    /// <summary>Token 预算服务 — 预算检查 + 格式化。数据存储委托给 IDbStore 实例。</summary>
    public static class TokenUsageTracker
    {
        public static IDbStore? Db { get; set; }
        public static event Action? OnUsageRecorded;

        private static IDbStore EnsureDb()
        {
            if (Db == null) throw new InvalidOperationException("TokenUsageTracker.Db 未注入");
            return Db;
        }

        public static string CurrentModel
        {
            get => EnsureDb().CurrentModel;
            set => EnsureDb().CurrentModel = value;
        }

        public static long TotalInputTokens => EnsureDb().TotalInputTokens;
        public static long TotalOutputTokens => EnsureDb().TotalOutputTokens;
        public static long TotalCacheReadTokens => EnsureDb().TotalCacheReadTokens;
        public static long TotalCacheCreateTokens => EnsureDb().TotalCacheCreateTokens;
        public static long TotalAllTokens => EnsureDb().TotalAllTokens;
        public static int TotalRequests => EnsureDb().TotalRequests;
        public static int TotalToolSuccess => EnsureDb().TotalToolSuccess;
        public static int TotalToolFailure => EnsureDb().TotalToolFailure;
        public static long TotalDurationMs => EnsureDb().TotalDurationMs;
        /// <summary>当前 SDK 上下文窗口大小（来自 assistant 消息 usage）</summary>
        public static long CurrentContextWindow { get; set; }
        /// <summary>最近一次请求的输入 token 数（反映当前上下文实际用量）</summary>
        public static long CurrentInputTokens { get; set; }
        /// <summary>最近一次请求的缓存命中 token 数（用于计算 per-turn 命中率）</summary>
        public static long CurrentCacheReadTokens { get; set; }
        /// <summary>最近一次请求的缓存新建 token 数</summary>
        public static long CurrentCacheCreateTokens { get; set; }
        /// <summary>当前上下文已使用 token 数（来自 ACP usage_update）</summary>
        public static long CurrentContextUsedTokens { get; set; }

        public static void Record(string model, long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            var db = EnsureDb();
            db.Record(model, inputTokens, outputTokens, cacheRead, cacheCreate, durationMs);
            var key = string.IsNullOrEmpty(model) ? "unknown" : model;
            GlobalModelUsageStore.Contribute(key, inputTokens, outputTokens, cacheRead, cacheCreate);
            try { OnUsageRecorded?.Invoke(); }
            catch (Exception ex) { CoreLog.Warn($"[TokenUsage] 推送预算更新失败: {ex.Message}"); }
        }

        public static void Record(long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            Record(CurrentModel, inputTokens, outputTokens, cacheRead, cacheCreate, durationMs);
        }

        /// <summary>仅追加会话耗时，不增加 TotalRequests 和 per-model 计数</summary>
        public static void AddDuration(long ms)
            => EnsureDb().AddDuration(ms);

        public static void RecordToolResult(bool isError)
            => EnsureDb().RecordToolResult(isError);

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

        public static string GetSummary()
        {
            var db = Db;
            if (db == null) return "Token 记录未初始化";
            if (db.TotalRequests == 0) return "暂无 Token 消耗记录";

            var sb = new StringBuilder();
            long totalTokens = db.TotalInputTokens + db.TotalOutputTokens;
            double avgDurationSec = db.TotalDurationMs / (double)db.TotalRequests / 1000.0;
            long totalInputWithCache = db.TotalInputTokens + db.TotalCacheReadTokens;
            double cacheHitRate = totalInputWithCache > 0
                ? (double)db.TotalCacheReadTokens / totalInputWithCache * 100.0
                : 0.0;

            sb.AppendLine("## Token 消耗统计");
            sb.AppendLine($"- 累计请求: {db.TotalRequests} 次 | 总耗时: {db.TotalDurationMs / 1000.0:F0} 秒 (均 {avgDurationSec:F1}s/次)");
            sb.AppendLine($"- 输入 Token: {db.TotalInputTokens:N0} | 缓存命中: {db.TotalCacheReadTokens:N0} ({cacheHitRate:F1}%) | 缓存新建: {db.TotalCacheCreateTokens:N0}");
            sb.AppendLine($"- 输出 Token: {db.TotalOutputTokens:N0}");
            sb.AppendLine($"- 合计 Token: {totalTokens:N0}");
            sb.AppendLine($"- 工具调用: {db.TotalToolSuccess + db.TotalToolFailure} 次 (成功 {db.TotalToolSuccess}, 失败 {db.TotalToolFailure})");

            var modelUsages = db.GetModelUsages();
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

        public static string GetCompactDisplay(long budgetLimit = 0)
        {
            var db = Db;
            if (db == null || db.TotalRequests == 0) return "Token: --";

            string fmt(long v) => v >= 1_000_000 ? $"{v / 1_000_000f:F1}M" :
                                  v >= 1_000 ? $"{v / 1_000f:F0}K" : v.ToString();

            // 本轮输入 = thisTurnInput + thisTurnCacheRead
            long thisTurnInput = CurrentInputTokens + CurrentCacheReadTokens;
            double thisTurnHitRate = thisTurnInput > 0
                ? (double)CurrentCacheReadTokens / thisTurnInput * 100.0
                : 0.0;

            int totalCalls = db.TotalToolSuccess + db.TotalToolFailure;
            string toolStr = totalCalls > 0
                ? $"工具 {db.TotalToolSuccess}✓{(db.TotalToolFailure > 0 ? $" {db.TotalToolFailure}✗" : "")} | "
                : "";

            string tokenPart;
            if (budgetLimit > 0)
            {
                double pct = (double)db.TotalAllTokens / budgetLimit * 100.0;
                int blocks = (int)(pct / 10.0);
                if (blocks > 10) blocks = 10;
                string bar = new string('█', blocks) + new string('░', 10 - blocks);
                tokenPart = $"入 {fmt(thisTurnInput)}({thisTurnHitRate:F0}%) | 总计 {fmt(db.TotalAllTokens)}/{fmt(budgetLimit)}({pct:F0}%) {bar}";
            }
            else
            {
                tokenPart = $"入 {fmt(thisTurnInput)}({thisTurnHitRate:F0}%) | 总计 {fmt(db.TotalAllTokens)}";
            }

            return $"{tokenPart} | {toolStr}{db.TotalRequests}轮";
        }
    }
}
