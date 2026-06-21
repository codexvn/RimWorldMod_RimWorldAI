namespace RimWorldAgent.Core.Data
{
    public sealed class ToolResultSnapshot
    {
        public string CacheKey { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string InputJson { get; set; } = "";
        public string OutputText { get; set; } = "";
        public long Version { get; set; }
    }
}
