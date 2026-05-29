using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_SetTempControl : ITool
    {
        public string Name => "set_temp_control";
        public string Description => "设置温控设备（空调/加热器）的目标温度或开关电源。用 thing_id 或坐标定位设备。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "温控设备的 thingIDNumber（与 pos_x/pos_y 二选一）" },
                pos_x = new { type = "integer", description = "设备 X 坐标" },
                pos_y = new { type = "integer", description = "设备 Y 坐标" },
                target_temp = new { type = "number", description = "目标温度（°C），不传则不修改" },
                power_on = new { type = "boolean", description = "是否开启电源，不传则不修改" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            int? thingId = null;
            int? posX = null, posY = null;
            float? targetTemp = null;
            bool? powerOn = null;

            if (args.Value.TryGetProperty("thing_id", out var jId) && jId.TryGetInt32(out var tid))
                thingId = tid;
            if (args.Value.TryGetProperty("pos_x", out var jx) && jx.TryGetInt32(out var px))
                posX = px;
            if (args.Value.TryGetProperty("pos_y", out var jy) && jy.TryGetInt32(out var py))
                posY = py;
            if (args.Value.TryGetProperty("target_temp", out var jTemp) && jTemp.ValueKind == JsonValueKind.Number)
                targetTemp = (float)jTemp.GetDouble();
            if (args.Value.TryGetProperty("power_on", out var jPwr))
                powerOn = jPwr.ValueKind != JsonValueKind.Null ? jPwr.GetBoolean() : null;

            if (thingId == null && (posX == null || posY == null))
                return ToolResult.Error("需要 thing_id 或 (pos_x + pos_y) 定位设备");
            if (targetTemp == null && powerOn == null)
                return ToolResult.Error("至少需要 target_temp 或 power_on 之一");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有可用地图。");

                // 定位设备
                Thing? device = null;
                if (thingId.HasValue)
                {
                    device = map.listerThings.AllThings
                        .FirstOrDefault(t => t.thingIDNumber == thingId.Value);
                    if (device == null)
                        return ToolResult.Error($"找不到 thingIDNumber={thingId} 的物品");
                }
                else
                {
                    var cell = new IntVec3(posX!.Value, 0, posY!.Value);
                    device = map.thingGrid.ThingsAt(cell)
                        .FirstOrDefault(t => t.TryGetComp<CompTempControl>() != null);
                    if (device == null)
                        return ToolResult.Error($"({posX},{posY}) 处没有温控设备");
                }

                var tempControl = device.TryGetComp<CompTempControl>();
                if (tempControl == null)
                    return ToolResult.Error($"{device.Label} 不是温控设备（缺少 CompTempControl）");

                var powerTrader = device.TryGetComp<CompPowerTrader>();
                var sb = new StringBuilder();
                sb.AppendLine($"## 温控设备: {device.Label} ({device.def.defName})");
                sb.AppendLine($"- 位置: ({device.Position.x},{device.Position.z})");

                // 修改前状态
                sb.AppendLine($"- 当前设定: {tempControl.TargetTemperature:F0}°C | 电源: {(powerTrader?.PowerOn ?? true ? "开" : "关")}");

                // 修改温度
                if (targetTemp.HasValue)
                {
                    float clamped = Math.Max(-270f, Math.Min(1000f, targetTemp.Value));
                    tempControl.TargetTemperature = clamped;
                    sb.AppendLine($"- 温度 → {clamped:F0}°C");
                }

                // 修改电源
                if (powerOn.HasValue && powerTrader != null)
                {
                    powerTrader.PowerOn = powerOn.Value;
                    sb.AppendLine($"- 电源 → {(powerOn.Value ? "开" : "关")}");
                }
                else if (powerOn.HasValue)
                {
                    sb.AppendLine("- 该设备无电源控制");
                }

                return ToolResult.Success(sb.ToString());
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args != null && args.Value.TryGetProperty("pos_x", out var jx) && jx.TryGetInt32(out var px)
                && args.Value.TryGetProperty("pos_y", out var jy) && jy.TryGetInt32(out var py))
                return (px, py, px, py);
            return null;
        }
    }
}
