using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>本地 JSON 文件持久化的 Token 存储。EXE 模式或需要跨进程持久化时使用。</summary>
    public class LocalFileTokenStore : ITokenStore
    {
        private readonly object _lock = new();
        private readonly string _filePath;
        private Dictionary<string, TokenModelUsage> _perModel = new();

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

        public LocalFileTokenStore(string? filePath = null)
        {
            _filePath = filePath ?? GetDefaultPath("RimWorldMCP_Tokens.json");
            Load();
        }

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

            Save();
            OnRecorded?.Invoke();
        }

        public void RecordToolResult(bool isError)
        {
            if (isError)
                Interlocked.Increment(ref TotalToolFailure);
            else
                Interlocked.Increment(ref TotalToolSuccess);
            Save();
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
            Save();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<PersistData>(json);
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
                        _perModel = data.PerModel ?? new Dictionary<string, TokenModelUsage>();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LocalFileToken] 加载失败: {ex.Message}");
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    var data = new PersistData
                    {
                        TotalInputTokens = Interlocked.Read(ref TotalInputTokens),
                        TotalOutputTokens = Interlocked.Read(ref TotalOutputTokens),
                        TotalCacheReadTokens = Interlocked.Read(ref TotalCacheReadTokens),
                        TotalCacheCreateTokens = Interlocked.Read(ref TotalCacheCreateTokens),
                        TotalRequests = TotalRequests,
                        TotalToolSuccess = TotalToolSuccess,
                        TotalToolFailure = TotalToolFailure,
                        TotalDurationMs = Interlocked.Read(ref TotalDurationMs),
                        CurrentModel = CurrentModel,
                        PerModel = _perModel
                    };
                    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LocalFileToken] 保存失败: {ex.Message}");
                }
            }
        }

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
            public Dictionary<string, TokenModelUsage> PerModel { get; set; } = new();
        }

        private static string GetDefaultPath(string fileName)
        {
            var dir = TaskBoard.SessionDir;
            if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, fileName);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWorldMCP", fileName);
        }
    }
}
