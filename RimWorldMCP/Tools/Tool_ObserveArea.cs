using System;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using RimWorldMCP.MapRendering;
using Verse;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 在地图上临时显示彩色半透明覆盖层，模拟 AI 正在观察某个区域。
    /// 标记会在指定 ticks 后自动消失，不会污染 plan_list。
    /// </summary>
    public class Tool_ObserveArea : ITool
    {
        public string Name => "observe_area";
        public string Description => "在地图上临时显示半透明覆盖层标记（自动过期）。用于指示 AI 正在关注的区域。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上角 X 坐标" },
                pos_y = new { type = "integer", description = "左上角 Y 坐标" },
                end_x = new { type = "integer", description = "右下角 X 坐标（可选，默认=pos_x）" },
                end_y = new { type = "integer", description = "右下角 Y 坐标（可选，默认=pos_y）" },
                label = new { type = "string", description = "标记标签（可选），如\"钢铁矿脉\"" },
                color = new
                {
                    type = "string",
                    description = "颜色名（可选）：White/Red/Green/Blue/Yellow/Purple/Cyan/Orange/Gray/Brown。默认 Cyan",
                    @default = "Cyan"
                },
                expire_ticks = new { type = "integer", description = "显示持续 tick 数（可选），默认 300（约5秒@1x）", @default = 300 }
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

            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;

            string label = "";
            if (args.Value.TryGetProperty("label", out var jLabel))
                label = jLabel.GetString() ?? "";

            string color = "Cyan";
            if (args.Value.TryGetProperty("color", out var jColor))
                color = jColor.GetString() ?? "Cyan";

            int expireTicks = 300;
            if (args.Value.TryGetProperty("expire_ticks", out var jExp) && jExp.TryGetInt32(out var et))
                expireTicks = Math.Max(1, Math.Min(et, 6000)); // 最大 6000 ticks (~100s)

            int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
            int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    AiObservationOverlay.Show(map, rect, label, color);

                    var size = $"{rect.Width}x{rect.Height}";
                    var labelStr = string.IsNullOrEmpty(label) ? "" : $" \"{label}\"";
                    return ToolResult.Success(
                        $"已在 ({minX},{minZ})~({maxX},{maxZ}) [{size}] 创建{labelStr}观察标记（{color}，{expireTicks} ticks）。");
                }
                catch (Exception ex) { return ToolResult.Error($"观察标记失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;
            return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
        }
    }
}
