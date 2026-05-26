using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TemperatureGrid : ITool
    {
        public string Name => "temperature_grid";
        public string Description => "获取指定范围的温度文本网格图。用于评估居住/种植适宜温度。";
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

        private static char TempChar(float t)
        {
            if (t < -20) return '█';
            if (t < 0) return '▓';
            if (t < 10) return '░';
            if (t < 21) return '.';
            if (t < 35) return '○';
            if (t < 60) return '◎';
            return '●';
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
                    sb.AppendLine($"## 温度 ({minX},{minZ})~({maxX},{maxZ}) [{w}x{h}]");
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            float t = GenTemperature.GetTemperatureForCell(new IntVec3(x, 0, z), map);
                            sb.Append(TempChar(t));
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine("█<-20°C  ▓-20~0  ░0~10  .10~21  ○21~35  ◎35~60  ●>60");
                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"温度查询失败: {ex.Message}"); }
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
