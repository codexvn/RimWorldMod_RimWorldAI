using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ToggleDoor : ITool, IRequiresAdvanceTick
    {
        public string Name => "toggle_door";
        public string Description => "切换门的保持开启状态（等效游戏内点击门的 Hold Open）。用于战斗堵门时保持门开启、或恢复日常自动开关。通过 thing_id 或 pos_x/pos_y 定位门。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "门的 thingIDNumber（从 list_doors 获取）" },
                pos_x = new { type = "integer", description = "门的 X 坐标（与 pos_y 配对，无 thing_id 时使用）" },
                pos_y = new { type = "integer", description = "门的 Y 坐标（与 pos_x 配对）" },
                hold_open = new { type = "boolean", description = "true=保持开启，false=恢复自动开关。不传则切换当前状态。" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数。请传 thing_id 或 pos_x/pos_y 定位门。");

            int? thingId = null;
            if (args.Value.TryGetProperty("thing_id", out var jId) && jId.TryGetInt32(out var id))
                thingId = id;

            int? posX = null, posY = null;
            if (args.Value.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var px)) posX = px;
            if (args.Value.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var py)) posY = py;

            if (thingId == null && (posX == null || posY == null))
                return ToolResult.Error("缺少门定位参数。请传 thing_id 或 pos_x+pos_y。");

            bool? wantHoldOpen = null;
            if (args.Value.TryGetProperty("hold_open", out var jHo))
            {
                if (jHo.ValueKind == JsonValueKind.True) wantHoldOpen = true;
                else if (jHo.ValueKind == JsonValueKind.False) wantHoldOpen = false;
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载存档。");

                    Building_Door? door = null;

                    if (thingId.HasValue)
                    {
                        foreach (var b in map.listerBuildings.allBuildingsColonist)
                        {
                            if (b.thingIDNumber == thingId.Value && b is Building_Door d)
                            { door = d; break; }
                        }
                        if (door == null) return ToolResult.Error($"未找到 ID={thingId} 的门。请用 list_doors 查看可用门。");
                    }
                    else
                    {
                        var cell = new IntVec3(posX!.Value, 0, posY!.Value);
                        if (!cell.InBounds(map)) return ToolResult.Error($"坐标 ({posX},{posY}) 在地图外。");
                        door = cell.GetDoor(map);
                        if (door == null) return ToolResult.Error($"坐标 ({posX},{posY}) 没有门。请用 list_doors 查看。");
                    }

                    bool targetState = wantHoldOpen ?? !door.HoldOpen;

                    // 等效于游戏内点击门的"保持开启"按钮：通过反射调用内部 setter
                    var prop = typeof(Building_Door).GetProperty("HoldOpen",
                        BindingFlags.Instance | BindingFlags.Public);
                    var setter = prop?.GetSetMethod(true);
                    if (setter == null)
                        return ToolResult.Error($"无法操作门 ID={door.thingIDNumber}（缺少 HoldOpen setter）。");

                    setter.Invoke(door, new object[] { targetState });

                    var desc = door.HoldOpen ? "保持开启（不会自动关闭）" : "自动（通过后自动关闭）";
                    return ToolResult.Success($"{door.def.label} (ID={door.thingIDNumber}, {door.Position.x},{door.Position.z}) → {desc}");
                }
                catch (Exception ex) { return ToolResult.Error($"门操作失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (args.Value.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var px)
                && args.Value.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var py))
                return (px, py, px, py);
            return null;
        }
    }
}
