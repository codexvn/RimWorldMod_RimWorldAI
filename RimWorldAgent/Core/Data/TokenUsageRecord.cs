namespace RimWorldAgent.Core.Data
{
    public class TokenUsageRecord
    {
        public string Model { get; set; } = "";
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreateTokens { get; set; }
        public long DurationMs { get; set; }
    }

    public class TokenModelUsage
    {
        public string Model { get; set; } = "";
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreateTokens { get; set; }
        public int RequestCount { get; set; }
        public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreateTokens;
    }
}
