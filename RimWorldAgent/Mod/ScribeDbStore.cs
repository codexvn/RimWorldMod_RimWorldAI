using System;
using System.Collections.Generic;
using System.Threading;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.Data;
using Verse;

namespace RimWorldAgent
{
    /// <summary>MOD 模式 Token 存储 — 使用 RimWorld Scribe_Values 持久化到存档</summary>
    public class ScribeDbStore : IDbStore
    {
        private readonly Dictionary<string, TokenModelUsage> _perModel = new();

        public event Action? OnRecorded;

        public string CurrentModel { get; set; } = "";

        // 总计字段 — MOD 模式不需要 Interlocked（所有操作在主线程）
        public long TotalInputTokens;
        public long TotalOutputTokens;
        public long TotalCacheReadTokens;
        public long TotalCacheCreateTokens;
        public int TotalRequests;
        public int TotalToolSuccess;
        public int TotalToolFailure;
        public long TotalDurationMs;

        long IDbStore.TotalInputTokens => TotalInputTokens;
        long IDbStore.TotalOutputTokens => TotalOutputTokens;
        long IDbStore.TotalCacheReadTokens => TotalCacheReadTokens;
        long IDbStore.TotalCacheCreateTokens => TotalCacheCreateTokens;
        long IDbStore.TotalAllTokens => TotalInputTokens + TotalOutputTokens + TotalCacheReadTokens + TotalCacheCreateTokens;
        int IDbStore.TotalRequests => TotalRequests;
        int IDbStore.TotalToolSuccess => TotalToolSuccess;
        int IDbStore.TotalToolFailure => TotalToolFailure;
        long IDbStore.TotalDurationMs => TotalDurationMs;

        public void Record(string model, long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            TotalInputTokens += inputTokens;
            TotalOutputTokens += outputTokens;
            TotalCacheReadTokens += cacheRead;
            TotalCacheCreateTokens += cacheCreate;
            TotalRequests++;
            TotalDurationMs += durationMs;

            if (!string.IsNullOrEmpty(model)) CurrentModel = model;

            var key = string.IsNullOrEmpty(model) ? "unknown" : model;
            if (!_perModel.TryGetValue(key, out var d))
            {
                d = new TokenModelUsage { Model = key };
                _perModel[key] = d;
            }
            d.InputTokens += inputTokens;
            d.OutputTokens += outputTokens;
            d.CacheReadTokens += cacheRead;
            d.CacheCreateTokens += cacheCreate;
            d.RequestCount++;

            OnRecorded?.Invoke();
        }

        public void RecordToolResult(bool isError)
        {
            if (isError) TotalToolFailure++;
            else TotalToolSuccess++;
        }

        public Dictionary<string, TokenModelUsage> GetModelUsages()
            => new(_perModel);

        public void Clear()
        {
            TotalInputTokens = 0; TotalOutputTokens = 0;
            TotalCacheReadTokens = 0; TotalCacheCreateTokens = 0;
            TotalRequests = 0; TotalToolSuccess = 0; TotalToolFailure = 0;
            TotalDurationMs = 0;
            CurrentModel = "";
            _perModel.Clear();
        }

        // ===== 持久化（Scribe 双向，ExposeData 中调用）=====

        public void ScribeExpose()
        {
            // 序列化当前数据 → 写入存档；反序列化 → 从存档读取
            string json = "";
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                json = System.Text.Json.JsonSerializer.Serialize(new PersistData
                {
                    TotalInputTokens = TotalInputTokens,
                    TotalOutputTokens = TotalOutputTokens,
                    TotalCacheReadTokens = TotalCacheReadTokens,
                    TotalCacheCreateTokens = TotalCacheCreateTokens,
                    TotalRequests = TotalRequests,
                    TotalToolSuccess = TotalToolSuccess,
                    TotalToolFailure = TotalToolFailure,
                    TotalDurationMs = TotalDurationMs,
                    CurrentModel = CurrentModel
                });
            }
            Scribe_Values.Look(ref json, "tokenUsageData", "");

            if (Scribe.mode == LoadSaveMode.LoadingVars && !string.IsNullOrEmpty(json))
            {
                try
                {
                    var data = System.Text.Json.JsonSerializer.Deserialize<PersistData>(json);
                    if (data != null)
                    {
                        TotalInputTokens = data.TotalInputTokens;
                        TotalOutputTokens = data.TotalOutputTokens;
                        TotalCacheReadTokens = data.TotalCacheReadTokens;
                        TotalCacheCreateTokens = data.TotalCacheCreateTokens;
                        TotalRequests = data.TotalRequests;
                        TotalToolSuccess = data.TotalToolSuccess;
                        TotalToolFailure = data.TotalToolFailure;
                        TotalDurationMs = data.TotalDurationMs;
                        CurrentModel = data.CurrentModel ?? "";
                    }
                }
                catch (Exception ex) { Log.Warning($"[ScribeDbStore] 加载失败: {ex.Message}"); }
            }
        }

        string IDbStore.GetCompactDisplay(long budgetLimit)
            => TokenUsageTracker.GetCompactDisplay(budgetLimit);

        string IDbStore.GetSummary(long budgetLimit)
            => TokenUsageTracker.GetSummary();

        private class PersistData
        {
            public long TotalInputTokens { get; set; }
            public long TotalOutputTokens { get; set; }
            public long TotalCacheReadTokens { get; set; }
            public long TotalCacheCreateTokens { get; set; }
            public int TotalRequests { get; set; }
            public int TotalToolSuccess { get; set; }
            public int TotalToolFailure { get; set; }
            public long TotalDurationMs { get; set; }
            public string CurrentModel { get; set; } = "";
        }
    }
}
