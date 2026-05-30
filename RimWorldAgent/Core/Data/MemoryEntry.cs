using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
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
}
