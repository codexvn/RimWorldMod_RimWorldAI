using System;
using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public interface ITokenStore
    {
        void Record(string model, long inputTokens, long outputTokens,
            long cacheReadTokens, long cacheCreateTokens, long durationMs);
        void RecordToolResult(bool isError);

        string CurrentModel { get; set; }
        long TotalInputTokens { get; }
        long TotalOutputTokens { get; }
        long TotalCacheReadTokens { get; }
        long TotalCacheCreateTokens { get; }
        long TotalAllTokens { get; }
        int TotalRequests { get; }
        int TotalToolSuccess { get; }
        int TotalToolFailure { get; }
        long TotalDurationMs { get; }

        Dictionary<string, TokenModelUsage> GetModelUsages();
        void Clear();
        event Action? OnRecorded;
    }
}
