using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent.Core.AgentRuntime
{
    public sealed class ToolResultContext
    {
        public string ToolName { get; set; } = "";
        public Dictionary<string, JsonElement> Args { get; } = new Dictionary<string, JsonElement>();
        public string CacheKey { get; set; } = "";
        public Dictionary<string, JsonElement> MetaData { get; } = new Dictionary<string, JsonElement>();
        public System.Func<ToolResultContext, Task<string>> CoreExec { get; set; }
            = _ => Task.FromResult("");

        public string RawResult { get; set; } = "";
        public string Output { get; set; } = "";
        public ToolResultSnapshot? BaselineSnapshot { get; set; }
        public string Mode { get; set; } = "full";
        public string Reason { get; set; } = "";
        public double Ratio { get; set; }
        public int ChangedLines { get; set; }
        public long Version { get; set; }
        public long BaseVersion { get; set; }
        public bool IsError { get; set; }
        public bool NoDiff { get; set; }
    }

    public interface IToolResultProcessor
    {
        int Order { get; }
        bool AppliesTo(string toolName);
        Task ProcessRequestAsync(ToolResultContext ctx);
        Task ProcessResponseAsync(ToolResultContext ctx);
    }

    public abstract class ToolResultProcessorBase : IToolResultProcessor
    {
        public abstract int Order { get; }
        public virtual bool AppliesTo(string toolName) => true;
        public virtual Task ProcessRequestAsync(ToolResultContext ctx) => Task.CompletedTask;
        public virtual Task ProcessResponseAsync(ToolResultContext ctx) => Task.CompletedTask;
    }
}
