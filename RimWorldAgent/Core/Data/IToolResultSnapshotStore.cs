namespace RimWorldAgent.Core.Data
{
    public interface IToolResultSnapshotStore
    {
        ToolResultSnapshot? Get(string cacheKey);
        void Upsert(ToolResultSnapshot snapshot);
        void Clear();
    }
}
