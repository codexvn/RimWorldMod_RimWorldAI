using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>本地 JSON 文件持久化的记忆存储。所有 Agent 记忆合并到单一文件。</summary>
    public class LocalFileMemoryStore : InMemoryMemoryStore
    {
        private readonly string _filePath;

        public LocalFileMemoryStore(string? filePath = null)
        {
            _filePath = filePath ?? GetDefaultPath("RimWorldMCP_Memory.json");
            Load();
        }

        public new void Append(string agentName, MemoryEntry entry)
        {
            base.Append(agentName, entry);
            Save();
        }

        public new void ReplaceAll(string agentName, List<MemoryEntry> entries)
        {
            base.ReplaceAll(agentName, entries);
            Save();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<Dictionary<string, AgentMemory>>(json);
                    if (data != null)
                    {
                        foreach (var kv in data)
                            _cache[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LocalFileMemory] 加载失败: {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LocalFileMemory] 保存失败: {ex.Message}");
            }
        }

        private static string GetDefaultPath(string fileName)
        {
            var dir = TaskBoard.SessionDir;
            if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, fileName);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWorldMCP", fileName);
        }
    }
}
