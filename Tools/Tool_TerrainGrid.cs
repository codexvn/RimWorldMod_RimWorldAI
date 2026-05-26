using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TerrainGrid : ITool
    {
        public string Name => "terrain_grid";
        public string Description => "获取指定范围的地形类型文本网格图。用于了解地表类型分布。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上 X 坐标" },
                pos_y = new { type = "integer", description = "左上 Y 坐标" },
                end_x = new { type = "integer", description = "右下 X 坐标（可选）" },
                end_y = new { type = "integer", description = "右下 Y 坐标（可选）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        private static char TerrainChar(TerrainDef t)
        {
            if (t == null) return '?';
            string dn = t.defName;
            if (dn.Contains("WaterDeep")) return '〰';
            if (dn.Contains("Water")) return '≈';
            if (dn.Contains("Marsh") || dn.Contains("Mud")) return '〰';
            if (dn.Contains("Sand")) return '·';
            if (dn.Contains("RichSoil")) return ':';
            if (dn.Contains("Soil")) return '.';
            if (dn.Contains("Gravel")) return ',';
            if (dn.Contains("Ice")) return '█';
            if (dn.Contains("Rock") && !dn.Contains("Sandstone") && !dn.Contains("Granite") && !dn.Contains("Limestone") && !dn.Contains("Slate") && !dn.Contains("Marble"))
                return '#';
            if (dn.Contains("Rough") || dn.Contains("Smooth")) return '█';
            if (dn.Contains("Carpet")) return '▣';
            if (dn.Contains("Wood") || dn.Contains("Board")) return '▤';
            if (dn.Contains("Tile") || dn.Contains("Flagstone") || dn.Contains("Concrete") || dn.Contains("Paved")) return '◇';
            return '.';
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");
            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEx)) jEx.TryGetInt32(out endX);
            if (args.Value.TryGetProperty("end_y", out var jEy)) jEy.TryGetInt32(out endY);
            int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
            int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);
            if (maxX - minX > 80 || maxZ - minZ > 80)
                return ToolResult.Error("范围不能超过 80x80");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");
                    int w = maxX - minX + 1, h = maxZ - minZ + 1;
                    var sb = new StringBuilder();
                    sb.AppendLine($"## 地形 ({minX},{minZ})~({maxX},{maxZ}) [{w}x{h}]");
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            var terrain = map.terrainGrid.TerrainAt(new IntVec3(x, 0, z));
                            sb.Append(TerrainChar(terrain));
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine(":沃土  .土壤  ,砾石  ·沙地  ≈浅水  〰深水/沼泽  █岩石  □混凝土  ▤木地板  ◇石砖  ▣地毯");
                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"地形查询失败: {ex.Message}"); }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            return (posX, posY);
        }
    }
}
