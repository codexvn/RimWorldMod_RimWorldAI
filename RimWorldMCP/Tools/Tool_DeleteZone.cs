using System;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_DeleteZone : ITool
    {
        public string Name => "delete_zone";
        public string Description => "删除指定位置的存储区或种植区。删除不可撤销，注意！⚠ 调用前应先使用 get_structure_layout 查看当前布局。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "区域内任意格 X 坐标" },
                pos_y = new { type = "integer", description = "区域内任意格 Y 坐标" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    var cell = new IntVec3(posX, 0, posY);
                    if (!cell.InBounds(map))
                        return ToolResult.Error($"坐标 ({posX}, {posY}) 超出地图范围");

                    var zone = map.zoneManager.ZoneAt(cell);
                    if (zone == null)
                        return ToolResult.Error($"坐标 ({posX}, {posY}) 处没有区域");

                    string label = zone.RenamableLabel;
                    int cellCount = zone.Cells.Count;
                    string typeName = zone is Zone_Stockpile ? "存储区"
                        : zone is Zone_Growing ? "种植区"
                        : zone.GetType().Name;

                    zone.Delete();

                    return ToolResult.Success($"已删除 {typeName}「{label}」（{cellCount} 格）");
                }
                catch (Exception ex) { return ToolResult.Error($"删除区域失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            return (posX, posY, posX, posY);
        }
    }
}
