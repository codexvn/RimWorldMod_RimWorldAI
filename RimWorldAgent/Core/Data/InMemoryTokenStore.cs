using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RimWorldAgent.Core.Data
{
    public class InMemoryTokenStore : ITokenStore
    {
        private readonly Dictionary<string, TokenModelUsage> _perModel = new();
        private readonly object _lock = new();

        public event Action? OnRecorded;

        public string CurrentModel { get; set; } = "";

        public long TotalInputTokens;
        public long TotalOutputTokens;
        public long TotalCacheReadTokens;
        public long TotalCacheCreateTokens;
        public int TotalRequests;
        public int TotalToolSuccess;
        public int TotalToolFailure;
        public long TotalDurationMs;

        long ITokenStore.TotalInputTokens => Interlocked.Read(ref TotalInputTokens);
        long ITokenStore.TotalOutputTokens => Interlocked.Read(ref TotalOutputTokens);
        long ITokenStore.TotalCacheReadTokens => Interlocked.Read(ref TotalCacheReadTokens);
        long ITokenStore.TotalCacheCreateTokens => Interlocked.Read(ref TotalCacheCreateTokens);
        long ITokenStore.TotalAllTokens => Interlocked.Read(ref TotalInputTokens)
            + Interlocked.Read(ref TotalOutputTokens)
            + Interlocked.Read(ref TotalCacheReadTokens)
            + Interlocked.Read(ref TotalCacheCreateTokens);
        int ITokenStore.TotalRequests => TotalRequests;
        int ITokenStore.TotalToolSuccess => TotalToolSuccess;
        int ITokenStore.TotalToolFailure => TotalToolFailure;
        long ITokenStore.TotalDurationMs => Interlocked.Read(ref TotalDurationMs);

        public void Record(string model, long inputTokens, long outputTokens,
            long cacheRead, long cacheCreate, long durationMs)
        {
            Interlocked.Add(ref TotalInputTokens, inputTokens);
            Interlocked.Add(ref TotalOutputTokens, outputTokens);
            Interlocked.Add(ref TotalCacheReadTokens, cacheRead);
            Interlocked.Add(ref TotalCacheCreateTokens, cacheCreate);
            Interlocked.Increment(ref TotalRequests);
            Interlocked.Add(ref TotalDurationMs, durationMs);

            if (!string.IsNullOrEmpty(model))
                CurrentModel = model;

            var key = string.IsNullOrEmpty(model) ? "unknown" : model;
            lock (_lock)
            {
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
            }

            OnRecorded?.Invoke();
        }

        public void RecordToolResult(bool isError)
        {
            if (isError)
                Interlocked.Increment(ref TotalToolFailure);
            else
                Interlocked.Increment(ref TotalToolSuccess);
        }

        public Dictionary<string, TokenModelUsage> GetModelUsages()
        {
            lock (_lock)
            {
                return _perModel.ToDictionary(kv => kv.Key, kv => new TokenModelUsage
                {
                    Model = kv.Value.Model,
                    InputTokens = kv.Value.InputTokens,
                    OutputTokens = kv.Value.OutputTokens,
                    CacheReadTokens = kv.Value.CacheReadTokens,
                    CacheCreateTokens = kv.Value.CacheCreateTokens,
                    RequestCount = kv.Value.RequestCount
                });
            }
        }

        public void Clear()
        {
            Interlocked.Exchange(ref TotalInputTokens, 0);
            Interlocked.Exchange(ref TotalOutputTokens, 0);
            Interlocked.Exchange(ref TotalCacheReadTokens, 0);
            Interlocked.Exchange(ref TotalCacheCreateTokens, 0);
            TotalRequests = 0;
            TotalToolSuccess = 0;
            TotalToolFailure = 0;
            Interlocked.Exchange(ref TotalDurationMs, 0);
            CurrentModel = "";
            lock (_lock) { _perModel.Clear(); }
        }
    }
}
