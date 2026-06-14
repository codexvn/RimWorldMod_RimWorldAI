using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_AllowItem : ITool, IRequiresAdvanceTick
    {
        public string Name => "allow_item";
        public string Description => "允许指定区域的物品，殖民者可以搬运和使用。复用游戏 Designator_Unforbid。坐标范围为闭区间（两端坐标均包含）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左下 X 坐标" },
                pos_y = new { type = "integer", description = "左下 Y 坐标" },
                end_x = new { type = "integer", description = "右上 X 坐标（可选）" },
                end_y = new { type = "integer", description = "右上 Y 坐标（可选）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var startX)) return ToolResult.Error("缺少 pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var startY)) return ToolResult.Error("缺少 pos_y");

            int endX = startX, endY = startY;
            if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey)) endY = ey;

            int minX = Math.Min(startX, endX), maxX = Math.Max(startX, endX);
            int minZ = Math.Min(startY, endY), maxZ = Math.Max(startY, endY);

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有可用地图。");
                var area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                area.ClipInsideMap(map);
                if (area.IsEmpty) return ToolResult.Error("指定范围完全在地图外");

                var des = new Designator_Unforbid();
                des.DesignateMultiCell(area.Cells);
                return ToolResult.Success($"已允许区域 ({minX},{minZ})→({maxX},{maxZ}) 内的物品。");
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var x)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var y)) return null;
            int ex = x, ey = y;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var _ex)) ex = _ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var _ey)) ey = _ey;
            return (Math.Min(x, ex), Math.Min(y, ey), Math.Max(x, ex), Math.Max(y, ey));
        }
}
}
