using System;
using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    /// <summary>纯内存记忆存储。不持久化，进程退出后丢失。</summary>
    public class InMemoryMemoryStore : IMemoryStore
    {
        protected readonly Dictionary<string, AgentMemory> _cache = new();

        public string GetMemoryText(string agentName)
        {
            var memory = GetOrCreate(agentName);
            if (memory.Entries.Count == 0) return "";
            var lines = new List<string> { "## 上次学到的经验" };
            foreach (var e in memory.Entries) lines.Add($"- Day {e.Day}: {e.Insight}");
            return string.Join("\n", lines);
        }

        public void Append(string agentName, MemoryEntry entry)
        {
            var memory = GetOrCreate(agentName);
            memory.Agent = agentName;
            memory.Entries.Add(entry);
            while (memory.Entries.Count > memory.MaxEntries)
                memory.Entries.RemoveAt(0);
            memory.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        }

        public void ReplaceAll(string agentName, List<MemoryEntry> entries)
        {
            _cache[agentName] = new AgentMemory
            {
                Agent = agentName,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Entries = entries,
                MaxEntries = 10
            };
        }

        protected AgentMemory GetOrCreate(string agentName)
        {
            if (!_cache.TryGetValue(agentName, out var memory))
            {
                memory = new AgentMemory { Agent = agentName };
                _cache[agentName] = memory;
            }
            return memory;
        }
    }
}
