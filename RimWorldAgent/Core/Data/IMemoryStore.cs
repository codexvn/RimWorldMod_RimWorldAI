using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public interface IMemoryStore
    {
        string GetMemoryText(string agentName);
        void Append(string agentName, MemoryEntry entry);
        void ReplaceAll(string agentName, List<MemoryEntry> entries);
    }
}
