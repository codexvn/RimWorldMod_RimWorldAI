using System;
using System.Linq;
using System.Text;
using DiffPlex;
using DiffPlex.Model;

namespace RimWorldAgent.Core.AgentRuntime
{
    public sealed class ToolResultDiffEngine
    {
        private const int ContextLines = 1;
        private const int MaxLineProduct = 200000;

        public ToolResultDiff Build(string oldText, string newText, long baseVersion, long version)
        {
            var oldLines = SplitLines(oldText);
            var newLines = SplitLines(newText);
            var lineProduct = (long)oldLines.Length * newLines.Length;
            if (lineProduct > MaxLineProduct)
            {
                return ToolResultDiff.TooLarge(oldLines.Length, newLines.Length);
            }

            var diff = Differ.Instance.CreateLineDiffs(oldText ?? "", newText ?? "", false);
            var changedLines = diff.DiffBlocks.Sum(block => block.DeleteCountA + block.InsertCountB);
            var ratioBase = Math.Max(oldLines.Length, newLines.Length);
            var ratio = ratioBase == 0 ? 0 : (double)changedLines / ratioBase;

            return new ToolResultDiff
            {
                ChangedLines = changedLines,
                Ratio = ratio,
                Text = BuildUnifiedDiff(diff, oldLines, newLines, baseVersion, version)
            };
        }

        private static string BuildUnifiedDiff(
            DiffResult diff,
            string[] oldLines,
            string[] newLines,
            long baseVersion,
            long version)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- v{baseVersion}");
            sb.AppendLine($"+++ v{version}");

            foreach (var block in diff.DiffBlocks)
            {
                var oldStart = Math.Max(0, block.DeleteStartA - ContextLines);
                var newStart = Math.Max(0, block.InsertStartB - ContextLines);
                var oldEnd = Math.Min(oldLines.Length, block.DeleteStartA + block.DeleteCountA + ContextLines);
                var newEnd = Math.Min(newLines.Length, block.InsertStartB + block.InsertCountB + ContextLines);

                sb.AppendLine($"@@ -{oldStart + 1},{oldEnd - oldStart} +{newStart + 1},{newEnd - newStart} @@");
                AppendContext(sb, oldLines, oldStart, block.DeleteStartA);
                AppendLines(sb, "-", oldLines, block.DeleteStartA, block.DeleteCountA);
                AppendLines(sb, "+", newLines, block.InsertStartB, block.InsertCountB);
                AppendContext(sb, newLines, block.InsertStartB + block.InsertCountB, newEnd);
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendContext(StringBuilder sb, string[] lines, int start, int end)
        {
            for (var i = start; i < end && i < lines.Length; i++)
                sb.Append(' ').AppendLine(lines[i]);
        }

        private static void AppendLines(StringBuilder sb, string prefix, string[] lines, int start, int count)
        {
            for (var i = start; i < start + count && i < lines.Length; i++)
                sb.Append(prefix).AppendLine(lines[i]);
        }

        private static string[] SplitLines(string text)
            => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    public sealed class ToolResultDiff
    {
        public string Text { get; set; } = "";
        public int ChangedLines { get; set; }
        public double Ratio { get; set; }
        public bool IsTooLarge { get; set; }
        public int OldLineCount { get; set; }
        public int NewLineCount { get; set; }

        public static ToolResultDiff TooLarge(int oldLineCount, int newLineCount)
            => new ToolResultDiff
            {
                IsTooLarge = true,
                OldLineCount = oldLineCount,
                NewLineCount = newLineCount,
                Ratio = 1
            };
    }
}
