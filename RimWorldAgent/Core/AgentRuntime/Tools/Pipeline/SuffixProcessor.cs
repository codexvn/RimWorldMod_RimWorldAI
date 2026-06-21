using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    public sealed class SuffixProcessor : ToolResultProcessorBase
    {
        public override int Order => 200;

        public override async Task ProcessResponseAsync(ToolResultContext ctx)
        {
            ctx.Output = (ctx.Output ?? "") + await ToolDispatcher.BuildModeSuffixAsync();
        }
    }
}
