using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public static class MemoryStore
    {
        public static IMemoryStore Instance { get; set; } = new InMemoryMemoryStore();

        public static string GetMemoryText(string agentName)
            => Instance.GetMemoryText(agentName);

        public static void Append(string agentName, MemoryEntry entry)
            => Instance.Append(agentName, entry);

        public static void ReplaceAll(string agentName, List<MemoryEntry> entries)
            => Instance.ReplaceAll(agentName, entries);
    }
}
