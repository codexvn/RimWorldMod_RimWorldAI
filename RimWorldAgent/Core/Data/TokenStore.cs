using System;
using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public static class TokenStore
    {
        public static ITokenStore Instance { get; set; } = new InMemoryTokenStore();

        // ===== 快捷属性 =====

        public static string CurrentModel
        {
            get => Instance.CurrentModel;
            set => Instance.CurrentModel = value;
        }

        public static long TotalInputTokens => Instance.TotalInputTokens;
        public static long TotalOutputTokens => Instance.TotalOutputTokens;
        public static long TotalCacheReadTokens => Instance.TotalCacheReadTokens;
        public static long TotalCacheCreateTokens => Instance.TotalCacheCreateTokens;
        public static long TotalAllTokens => Instance.TotalAllTokens;
        public static int TotalRequests => Instance.TotalRequests;
        public static int TotalToolSuccess => Instance.TotalToolSuccess;
        public static int TotalToolFailure => Instance.TotalToolFailure;
        public static long TotalDurationMs => Instance.TotalDurationMs;

        // ===== OnRecorded 事件转发 =====

        public static event Action? OnRecorded
        {
            add => Instance.OnRecorded += value;
            remove => Instance.OnRecorded -= value;
        }

        // ===== 方法委托 =====

        public static void Record(string model, long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
            => Instance.Record(model, inputTokens, outputTokens, cacheRead, cacheCreate, durationMs);

        public static void RecordToolResult(bool isError)
            => Instance.RecordToolResult(isError);

        public static Dictionary<string, TokenModelUsage> GetModelUsages()
            => Instance.GetModelUsages();

        public static void Clear()
            => Instance.Clear();
    }
}
