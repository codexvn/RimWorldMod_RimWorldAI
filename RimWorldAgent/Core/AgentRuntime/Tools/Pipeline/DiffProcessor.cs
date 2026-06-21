using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent.Core.AgentRuntime
{
    public sealed class DiffProcessor : ToolResultProcessorBase
    {
        private readonly IToolResultSnapshotStore _snapshotStore;
        private readonly ToolResultDiffEngine _diffEngine;
        private readonly bool _enabled;
        private readonly double _threshold;
        private static long _lastVersion;

        public DiffProcessor(
            IToolResultSnapshotStore snapshotStore,
            ToolResultDiffEngine diffEngine,
            bool enabled,
            double threshold)
        {
            _snapshotStore = snapshotStore;
            _diffEngine = diffEngine;
            _enabled = enabled;
            _threshold = Math.Max(0, Math.Min(1, threshold));
        }

        public override int Order => 100;

        public override Task ProcessRequestAsync(ToolResultContext ctx)
        {
            ctx.NoDiff = ReadNoDiff(ctx.MetaData);
            return Task.CompletedTask;
        }

        public override Task ProcessResponseAsync(ToolResultContext ctx)
        {
            ctx.Version = NextVersion();

            if (string.IsNullOrWhiteSpace(ctx.CacheKey))
            {
                WriteFull(ctx, "no_cache_key");
                return Task.CompletedTask;
            }

            var baseline = _snapshotStore.Get(ctx.CacheKey);
            ctx.BaselineSnapshot = baseline;

            if (ctx.NoDiff)
            {
                Upsert(ctx);
                WriteFull(ctx, "no_diff_param");
                return Task.CompletedTask;
            }

            if (!_enabled)
            {
                Upsert(ctx);
                WriteFull(ctx, "disabled");
                return Task.CompletedTask;
            }

            if (baseline == null)
            {
                Upsert(ctx);
                WriteFull(ctx, "no_baseline");
                return Task.CompletedTask;
            }

            ctx.BaseVersion = baseline.Version;
            var diff = _diffEngine.Build(baseline.OutputText, ctx.RawResult, baseline.Version, ctx.Version);
            ctx.ChangedLines = diff.ChangedLines;
            ctx.Ratio = diff.Ratio;

            Upsert(ctx);

            if (diff.IsTooLarge)
            {
                WriteFull(ctx, "too_large");
                return Task.CompletedTask;
            }

            if (diff.Ratio > _threshold)
            {
                WriteFull(ctx, "ratio_exceeded");
                return Task.CompletedTask;
            }

            ctx.Mode = "patch";
            ctx.Reason = "";
            ctx.Output = BuildPatchOutput(ctx, diff.Text);
            return Task.CompletedTask;
        }

        private void Upsert(ToolResultContext ctx)
        {
            _snapshotStore.Upsert(new ToolResultSnapshot
            {
                CacheKey = ctx.CacheKey,
                ToolName = ctx.ToolName,
                InputJson = JsonSerializer.Serialize(ctx.Args),
                OutputText = ctx.RawResult,
                Version = ctx.Version
            });
        }

        private static bool ReadNoDiff(System.Collections.Generic.Dictionary<string, JsonElement> metaData)
        {
            if (!metaData.TryGetValue("xxprocess", out var xxprocess) || xxprocess.ValueKind != JsonValueKind.Object)
                return false;

            if (xxprocess.TryGetProperty("noDiff", out var noDiff) && noDiff.ValueKind == JsonValueKind.True)
                return true;

            return xxprocess.TryGetProperty("nodiff", out var nodiff) && nodiff.ValueKind == JsonValueKind.True;
        }

        private static void WriteFull(ToolResultContext ctx, string reason)
        {
            ctx.Mode = "full";
            ctx.Reason = reason;
            ctx.Output = BuildFullOutput(ctx);
        }

        private static string BuildFullOutput(ToolResultContext ctx)
        {
            var sb = new StringBuilder();
            sb.Append($"**version**: {ctx.Version} | **mode**: full | **reason**: {ctx.Reason}");
            if (!string.IsNullOrWhiteSpace(ctx.CacheKey))
                sb.Append($" | **cacheKey**: {ctx.CacheKey}");
            sb.AppendLine();
            sb.Append(ctx.RawResult ?? "");
            return sb.ToString();
        }

        private static string BuildPatchOutput(ToolResultContext ctx, string diffText)
        {
            var ratio = ctx.Ratio.ToString("0.###", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append($"**version**: {ctx.Version} | **baseVersion**: {ctx.BaseVersion} | **mode**: patch");
            sb.Append($" | **changedLines**: {ctx.ChangedLines} | **ratio**: {ratio} | **cacheKey**: {ctx.CacheKey}");
            sb.AppendLine();
            sb.AppendLine("```diff");
            sb.AppendLine(diffText);
            sb.Append("```");
            return sb.ToString();
        }

        private static long NextVersion()
        {
            while (true)
            {
                var previous = Interlocked.Read(ref _lastVersion);
                var current = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var next = current > previous ? current : previous + 1;
                if (Interlocked.CompareExchange(ref _lastVersion, next, previous) == previous)
                    return next;
            }
        }
    }
}
