using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace RimWorldMCP
{
    public class ModelUsageData
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreateTokens { get; set; }
        public int RequestCount { get; set; }
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreateTokens;
    }

    /// <summary>全局模型用量汇总，跨存档累加，JSON 文件持久化</summary>
    public static class GlobalModelUsageStore
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, ModelUsageData> _allModels = new Dictionary<string, ModelUsageData>();

        private static string FilePath => Path.Combine(
            Application.persistentDataPath, "RimWorldMCP_ModelUsage.json");

        public static Dictionary<string, ModelUsageData> AllModels
        {
            get
            {
                lock (_lock)
                {
                    return new Dictionary<string, ModelUsageData>(_allModels);
                }
            }
        }

        public static void Contribute(string model, long input, long output,
            long cacheRead, long cacheCreate)
        {
            if (string.IsNullOrEmpty(model)) return;
            lock (_lock)
            {
                if (!_allModels.TryGetValue(model, out var data))
                {
                    data = new ModelUsageData();
                    _allModels[model] = data;
                }
                data.InputTokens += input;
                data.OutputTokens += output;
                data.CacheReadTokens += cacheRead;
                data.CacheCreateTokens += cacheCreate;
                data.RequestCount++;
            }
            Save();
        }

        public static long TotalAllTokens()
        {
            lock (_lock)
            {
                long total = 0;
                foreach (var kv in _allModels)
                    total += kv.Value.TotalTokens;
                return total;
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _allModels.Clear();
            }
            Save();
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    var json = JsonSerializer.Serialize(_allModels, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    File.WriteAllText(FilePath, json);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[GlobalModelUsage] 保存失败: {ex.Message}");
                }
            }
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath);
                        var data = JsonSerializer.Deserialize<Dictionary<string, ModelUsageData>>(json);
                        _allModels = data ?? new Dictionary<string, ModelUsageData>();
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[GlobalModelUsage] 加载失败: {ex.Message}");
                    _allModels = new Dictionary<string, ModelUsageData>();
                }
            }
        }
    }
}
