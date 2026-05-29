using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    public enum BudgetStatus { Ok, Warning, Critical, Exceeded }

    /// <summary>Token 消耗追踪器 — 按存档 + 按模型追踪，JSON 独立文件持久化，每次 Record() 实时写入</summary>
    public static class TokenUsageTracker
    {
        // 合计字段
        public static long TotalInputTokens;
        public static long TotalOutputTokens;
        public static long TotalCacheReadTokens;
        public static long TotalCacheCreateTokens;
        public static int TotalRequests;
        public static int TotalToolSuccess;
        public static int TotalToolFailure;
        public static long TotalDurationMs;

        // 当前存档各模型用量
        public static Dictionary<string, ModelUsageData> PerModelUsages = new Dictionary<string, ModelUsageData>();

        // 当前会话模型名（从 SDK init 消息获取）
        public static string CurrentModel = "";

        // 当前 sessionId，用于区分存档
        private static string _sessionId = "";

        public static long TotalAllTokens =>
            TotalInputTokens + TotalOutputTokens + TotalCacheReadTokens + TotalCacheCreateTokens;

        public static void Record(string model, long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            // 更新合计
            Interlocked.Add(ref TotalInputTokens, inputTokens);
            Interlocked.Add(ref TotalOutputTokens, outputTokens);
            Interlocked.Add(ref TotalCacheReadTokens, cacheRead);
            Interlocked.Add(ref TotalCacheCreateTokens, cacheCreate);
            Interlocked.Increment(ref TotalRequests);
            Interlocked.Add(ref TotalDurationMs, durationMs);

            // 更新当前模型名
            if (!string.IsNullOrEmpty(model))
                CurrentModel = model;

            // 更新按模型统计
            var key = string.IsNullOrEmpty(model) ? "unknown" : model;
            lock (PerModelUsages)
            {
                if (!PerModelUsages.TryGetValue(key, out var data))
                {
                    data = new ModelUsageData();
                    PerModelUsages[key] = data;
                }
                data.InputTokens += inputTokens;
                data.OutputTokens += outputTokens;
                data.CacheReadTokens += cacheRead;
                data.CacheCreateTokens += cacheCreate;
                data.RequestCount++;
            }

            // 同步写入全局汇总
            GlobalModelUsageStore.Contribute(key, inputTokens, outputTokens, cacheRead, cacheCreate);

            // 实时写入 JSON 文件
            Save();

            // 实时刷新 UI 预算状态（不等游戏事件推送）
            RefreshBudgetDisplay();
        }

        /// <summary>Record() 后立即刷新 ChatDisplayState 预算字段</summary>
        private static void RefreshBudgetDisplay()
        {
            var settings = RimWorldMCPMod.Instance?.Settings;
            if (settings == null) return;
            var limit = settings.TokenBudgetLimit;
            ChatDisplayState.CurrentBudgetStatus = CheckBudget(limit);
            ChatDisplayState.CurrentBudgetPercent = GetBudgetUsagePercent(limit);
            ChatDisplayState.CurrentBudgetText = GetCompactDisplay(limit);

            // 推送 budget 更新给 companion（web 页面实时刷新）
            _ = CCClient.SendEvent("budget-update", new { used = TotalAllTokens });
        }

        /// <summary>兼容旧的无模型名调用</summary>
        public static void Record(long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            Record(CurrentModel, inputTokens, outputTokens, cacheRead, cacheCreate, durationMs);
        }

        public static void RecordToolResult(bool isError)
        {
            if (isError)
                Interlocked.Increment(ref TotalToolFailure);
            else
                Interlocked.Increment(ref TotalToolSuccess);
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

        // ===== 持久化 (JSON 独立文件，按 sessionId) =====

        public static void Init(string sessionId)
        {
            _sessionId = sessionId;
            Load();
        }

        private static string FilePath => Path.Combine(
            Application.persistentDataPath, $"RimWorldMCP_TokenUsage_{_sessionId}.json");

        private static readonly object _saveLock = new();

        public static void Save()
        {
            if (string.IsNullOrEmpty(_sessionId)) return;
            lock (_saveLock)
            {
                try
                {
                    var payload = new
                    {
                        totalInputTokens = TotalInputTokens,
                        totalOutputTokens = TotalOutputTokens,
                        totalCacheReadTokens = TotalCacheReadTokens,
                        totalCacheCreateTokens = TotalCacheCreateTokens,
                        totalRequests = TotalRequests,
                        totalToolSuccess = TotalToolSuccess,
                        totalToolFailure = TotalToolFailure,
                        totalDurationMs = TotalDurationMs,
                        currentModel = CurrentModel,
                        perModelUsages = PerModelUsages
                    };
                    string json = JsonSerializer.Serialize(payload);
                    File.WriteAllText(FilePath, json, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[TokenUsage] 保存失败: {ex.Message}");
                }
            }
        }

        public static void Load()
        {
            lock (_saveLock)
            {
                try
                {
                    if (!File.Exists(FilePath)) return;

                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("TotalInputTokens", out var jIt)) TotalInputTokens = jIt.GetInt64();
                    if (root.TryGetProperty("TotalOutputTokens", out var jOt)) TotalOutputTokens = jOt.GetInt64();
                    if (root.TryGetProperty("TotalCacheReadTokens", out var jCr)) TotalCacheReadTokens = jCr.GetInt64();
                    if (root.TryGetProperty("TotalCacheCreateTokens", out var jCc)) TotalCacheCreateTokens = jCc.GetInt64();
                    if (root.TryGetProperty("TotalRequests", out var jRq)) TotalRequests = jRq.GetInt32();
                    if (root.TryGetProperty("TotalToolSuccess", out var jTs)) TotalToolSuccess = jTs.GetInt32();
                    if (root.TryGetProperty("TotalToolFailure", out var jTf)) TotalToolFailure = jTf.GetInt32();
                    if (root.TryGetProperty("TotalDurationMs", out var jDu)) TotalDurationMs = jDu.GetInt64();
                    if (root.TryGetProperty("CurrentModel", out var jCm)) CurrentModel = jCm.GetString() ?? "";

                    if (root.TryGetProperty("PerModelUsages", out var perModel))
                    {
                        PerModelUsages = new Dictionary<string, ModelUsageData>();
                        foreach (var kv in perModel.EnumerateObject())
                        {
                            var d = kv.Value;
                            PerModelUsages[kv.Name] = new ModelUsageData
                            {
                                InputTokens = d.TryGetProperty("InputTokens", out var i) ? i.GetInt64() : 0,
                                OutputTokens = d.TryGetProperty("OutputTokens", out var o) ? o.GetInt64() : 0,
                                CacheReadTokens = d.TryGetProperty("CacheReadTokens", out var cr) ? cr.GetInt64() : 0,
                                CacheCreateTokens = d.TryGetProperty("CacheCreateTokens", out var cc) ? cc.GetInt64() : 0,
                                RequestCount = d.TryGetProperty("RequestCount", out var rc) ? rc.GetInt32() : 0,
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[TokenUsage] 加载失败: {ex.Message}");
                }
            }
        }

        // ===== 格式化输出 =====

        public static string GetSummary()
        {
            if (TotalRequests == 0)
                return "暂无 Token 消耗记录";

            var sb = new StringBuilder();
            long totalTokens = TotalInputTokens + TotalOutputTokens;
            double avgDurationSec = TotalDurationMs / (double)TotalRequests / 1000.0;
            long totalInputWithCache = TotalInputTokens + TotalCacheCreateTokens + TotalCacheReadTokens;
            double cacheHitRate = totalInputWithCache > 0
                ? (double)TotalCacheReadTokens / totalInputWithCache * 100.0
                : 0.0;

            sb.AppendLine("## Token 消耗统计");
            sb.AppendLine($"- 累计请求: {TotalRequests} 次 | 总耗时: {TotalDurationMs / 1000.0:F0} 秒 (均 {avgDurationSec:F1}s/次)");
            sb.AppendLine($"- 输入 Token: {TotalInputTokens:N0} | 缓存命中: {TotalCacheReadTokens:N0} ({cacheHitRate:F1}%) | 缓存新建: {TotalCacheCreateTokens:N0}");
            sb.AppendLine($"- 输出 Token: {TotalOutputTokens:N0}");
            sb.AppendLine($"- 合计 Token: {totalTokens:N0}");
            sb.AppendLine($"- 工具调用: {TotalToolSuccess + TotalToolFailure} 次 (成功 {TotalToolSuccess}, 失败 {TotalToolFailure})");

            // 按模型细分
            lock (PerModelUsages)
            {
                if (PerModelUsages.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("### 按模型");
                    foreach (var kv in PerModelUsages.OrderByDescending(kv => kv.Value.TotalTokens))
                    {
                        var d = kv.Value;
                        sb.AppendLine($"- **{kv.Key}**: 合计 {d.TotalTokens:N0} | 入 {d.InputTokens:N0} | 出 {d.OutputTokens:N0} | 缓存 {d.CacheReadTokens:N0} | {d.RequestCount} 次");
                    }
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
            long totalInputWithCache = TotalInputTokens + TotalCacheCreateTokens + TotalCacheReadTokens;
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
