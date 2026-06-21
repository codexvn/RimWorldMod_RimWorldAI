using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public sealed class MemoryToolResultSnapshotStore : IToolResultSnapshotStore
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, ToolResultSnapshot> _snapshots = new Dictionary<string, ToolResultSnapshot>();

        public ToolResultSnapshot? Get(string cacheKey)
        {
            lock (_lock)
            {
                return _snapshots.TryGetValue(cacheKey, out var snapshot)
                    ? Copy(snapshot)
                    : null;
            }
        }

        public void Upsert(ToolResultSnapshot snapshot)
        {
            lock (_lock)
            {
                _snapshots[snapshot.CacheKey] = Copy(snapshot);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _snapshots.Clear();
            }
        }

        private static ToolResultSnapshot Copy(ToolResultSnapshot snapshot)
            => new ToolResultSnapshot
            {
                CacheKey = snapshot.CacheKey,
                ToolName = snapshot.ToolName,
                InputJson = snapshot.InputJson,
                OutputText = snapshot.OutputText,
                Version = snapshot.Version
            };
    }
}
