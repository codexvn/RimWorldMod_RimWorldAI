 using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RimWorldMCP.AgentRuntime
{
    public class MemoryEntry
    {
        public int Day { get; set; }
        public string Insight { get; set; } = "";
        public string Type { get; set; } = "general";
    }

    public class AgentMemory
    {
        public string Agent { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public List<MemoryEntry> Entries { get; set; } = new();
        public int MaxEntries { get; set; } = 10;
    }

    /// <summary>
    /// 管理每个 Agent 的记忆文件（Reflection Loop 产出），JSON 持久化到 session 目录。
    /// </summary>
    public static class MemoryManager
    {
        private static readonly Dictionary<string, AgentMemory> _cache = new();

        /// <summary>
        /// 获取 Agent 的记忆内容，注入 Prompt 的 [Memory] 段落。
        /// </summary>
        public static string GetMemoryText(string agentName)
        {
            var memory = Load(agentName);
            if (memory.Entries.Count == 0) return "";

            var lines = new List<string> { "## 上次学到的经验" };
            foreach (var e in memory.Entries)
                lines.Add($"- Day {e.Day}: {e.Insight}");
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Agent 结束前追加新经验，超过 MaxEntries 滚动淘汰最旧的。
        /// </summary>
        public static void Append(string agentName, MemoryEntry entry)
        {
            var memory = Load(agentName);
            memory.Agent = agentName;
            memory.Entries.Add(entry);
            while (memory.Entries.Count > memory.MaxEntries)
                memory.Entries.RemoveAt(0);
            memory.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save(agentName, memory);
        }

        /// <summary>
        /// Agent 结束前由 LLM 自行总结，批量覆盖记忆文件。
        /// </summary>
        public static void ReplaceAll(string agentName, List<MemoryEntry> entries)
        {
            var memory = new AgentMemory
            {
                Agent = agentName,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Entries = entries,
                MaxEntries = 10
            };
            Save(agentName, memory);
        }

        private static AgentMemory Load(string agentName)
        {
            if (_cache.TryGetValue(agentName, out var cached)) return cached;
            var path = FilePath(agentName);
            if (!File.Exists(path)) return new AgentMemory { Agent = agentName };
            try
            {
                var json = File.ReadAllText(path);
                var memory = JsonSerializer.Deserialize<AgentMemory>(json) ?? new AgentMemory();
                _cache[agentName] = memory;
                return memory;
            }
            catch
            {
                return new AgentMemory { Agent = agentName };
            }
        }

        private static void Save(string agentName, AgentMemory memory)
        {
            _cache[agentName] = memory;
            var path = FilePath(agentName);
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(memory, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                McpLog.Error($"[MemoryManager] 保存 {agentName} 失败: {ex.Message}");
            }
        }

        private static string FilePath(string agentName)
        {
            var dir = TaskBoard.SessionDir;
            if (string.IsNullOrEmpty(dir)) return "";
            return Path.Combine(dir, $"{agentName}-memory.json");
        }
    }
}
