using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    /// <summary>防御位置内存存储</summary>
    public static class DefendPointStore
    {
        public class DefendPoint
        {
            public int PosX, PosY;
            public string Label = "";
            public int Priority = 1; // 1=主要 2=备用
        }

        public static readonly List<DefendPoint> Points = new List<DefendPoint>();
    }

    public class Tool_DefendPosition : ITool, IRequiresAdvanceTick
    {
        public string Name => "defend_position";
        public string Description => "设置、查询、清除殖民地防御位置（内存存储）。action: set/list/remove/clear。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                action = new
                {
                    type = "string",
                    description = "操作类型",
                    @enum = new[] { "set", "list", "remove", "clear" }
                },
                positions = new
                {
                    type = "array",
                    description = "防御位置列表（set/remove 时必填）",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            pos_x = new { type = "integer", description = "X 坐标" },
                            pos_y = new { type = "integer", description = "Y 坐标" },
                            label = new { type = "string", description = "标签（可选，如: 南门）" },
                            priority = new { type = "integer", description = "优先级: 1=主要, 2=备用（默认1）" }
                        },
                        required = new[] { "pos_x", "pos_y" }
                    }
                }
            },
            required = new[] { "action" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return Task.FromResult(ToolResult.Error("缺少参数"));
            if (!args.Value.TryGetProperty("action", out var jAction))
                return Task.FromResult(ToolResult.Error("缺少必填参数: action"));
            var action = jAction.GetString() ?? "";

            try
            {
                switch (action)
                {
                    case "set":
                        return Task.FromResult(HandleSet(args.Value));
                    case "list":
                        return Task.FromResult(HandleList());
                    case "remove":
                        return Task.FromResult(HandleRemove(args.Value));
                    case "clear":
                        return Task.FromResult(HandleClear());
                    default:
                        return Task.FromResult(ToolResult.Error($"未知 action: {action}。可选: set, list, remove, clear"));
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Error($"防御位置操作失败: {ex.Message}"));
            }
        }

        private ToolResult HandleSet(JsonElement args)
        {
            if (!args.TryGetProperty("positions", out var jPositions) || jPositions.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少必填参数: positions（需为数组）");

            int added = 0;
            var labels = new List<string>();
            foreach (var jp in jPositions.EnumerateArray())
            {
                if (!jp.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var px)) continue;
                if (!jp.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var py)) continue;

                string label = "";
                jp.TryGetProperty("label", out var jL);
                if (jL.ValueKind == JsonValueKind.String) label = jL.GetString() ?? "";

                int priority = 1;
                if (jp.TryGetProperty("priority", out var jP) && jP.TryGetInt32(out var p)) priority = p;

                // 去重：同坐标不重复添加
                if (DefendPointStore.Points.Any(dp => dp.PosX == px && dp.PosY == py)) continue;

                DefendPointStore.Points.Add(new DefendPointStore.DefendPoint
                {
                    PosX = px, PosY = py, Label = label, Priority = priority
                });
                added++;
                labels.Add(string.IsNullOrEmpty(label) ? $"({px},{py})" : $"{label}({px},{py})");
            }

            return ToolResult.Success($"已设置 {added} 个防御位置: {string.Join(", ", labels)}（共 {DefendPointStore.Points.Count} 个）");
        }

        private ToolResult HandleList()
        {
            var points = DefendPointStore.Points;
            if (points.Count == 0) return ToolResult.Success("（无防御位置）");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"### 防御位置 ({points.Count} 个)");
            sb.AppendLine("| # | 坐标 | 标签 | 优先级 |");
            sb.AppendLine("|---|------|------|--------|");
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                string label = string.IsNullOrEmpty(p.Label) ? "-" : p.Label;
                string prio = p.Priority == 1 ? "主要" : "备用";
                sb.AppendLine($"| {i + 1} | ({p.PosX},{p.PosY}) | {label} | {prio} |");
            }
            return ToolResult.Success(sb.ToString().TrimEnd());
        }

        private ToolResult HandleRemove(JsonElement args)
        {
            if (!args.TryGetProperty("positions", out var jPositions) || jPositions.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少必填参数: positions（需为数组）");

            int removed = 0;
            foreach (var jp in jPositions.EnumerateArray())
            {
                if (!jp.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var px)) continue;
                if (!jp.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var py)) continue;

                removed += DefendPointStore.Points.RemoveAll(dp => dp.PosX == px && dp.PosY == py);
            }

            return ToolResult.Success($"已移除 {removed} 个防御位置（共 {DefendPointStore.Points.Count} 个）");
        }

        private ToolResult HandleClear()
        {
            int count = DefendPointStore.Points.Count;
            DefendPointStore.Points.Clear();
            return ToolResult.Success($"已清除全部 {count} 个防御位置。");
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var points = DefendPointStore.Points;
            if (points.Count == 0) return null;
            int minX = points.Min(p => p.PosX), minZ = points.Min(p => p.PosY);
            int maxX = points.Max(p => p.PosX), maxZ = points.Max(p => p.PosY);
            return (minX, minZ, maxX, maxZ);
        }
    }
}
