using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime
{
    public sealed class ToolResultPipeline
    {
        private readonly List<IToolResultProcessor> _processors;

        public ToolResultPipeline(IEnumerable<IToolResultProcessor> processors)
        {
            _processors = processors
                .OrderBy(processor => processor.Order)
                .ToList();
        }

        public async Task<ToolResultContext> ExecuteAsync(ToolResultContext ctx)
        {
            var processors = _processors
                .Where(processor => processor.AppliesTo(ctx.ToolName))
                .ToList();

            foreach (var processor in processors)
                await processor.ProcessRequestAsync(ctx);

            ctx.RawResult = await ctx.CoreExec(ctx);
            ctx.Output = ctx.RawResult;

            foreach (var processor in processors)
                await processor.ProcessResponseAsync(ctx);

            return ctx;
        }
    }
}
