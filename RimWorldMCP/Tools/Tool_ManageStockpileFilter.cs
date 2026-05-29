using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ManageStockpileFilter : ITool
    {
        public string Name => "manage_stockpile_filter";
        public string Description => "管理存储区允许的物品类型。通过 def_name 指定 ThingDef，通过 allow 决定添加或移除。⚠ 调用前应先使用 get_structure_layout 查看当前布局。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "存储区范围内任意格 X 坐标" },
                pos_y = new { type = "integer", description = "存储区范围内任意格 Y 坐标" },
                def_name = new { type = "string", description = "ThingDef 的 defName，如 WoodLog、Steel、MeleeWeapon_PlasteelKnife" },
                allow = new { type = "boolean", description = "true 允许该物品，false 禁止该物品" }
            },
            required = new[] { "pos_x", "pos_y", "def_name", "allow" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");
            if (!args.Value.TryGetProperty("def_name", out var jDef) || string.IsNullOrEmpty(jDef.GetString()))
                return ToolResult.Error("缺少必填参数: def_name");
            if (!args.Value.TryGetProperty("allow", out var jAllow) || jAllow.ValueKind != JsonValueKind.True && jAllow.ValueKind != JsonValueKind.False)
                return ToolResult.Error("缺少必填参数: allow（true/false）");

            var defName = jDef.GetString()!;
            bool allow = jAllow.GetBoolean();

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
                        return ToolResult.Error($"指定位置 ({posX}, {posY}) 没有存储区");

                    var stockpile = zone as Zone_Stockpile;
                    if (stockpile == null)
                        return ToolResult.Error($"指定位置 ({posX}, {posY}) 不是存储区（当前是 {zone.GetType().Name}）");

                    var thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                    if (thingDef == null)
                        return ToolResult.Error($"未找到 ThingDef: {defName}。请使用 search_thing_def 查询可用 defName");

                    stockpile.settings.filter.SetAllow(thingDef, allow);

                    var allowedCount = stockpile.settings.filter.AllowedDefCount;
                    var sb = new StringBuilder();
                    sb.Append(allow ? "已添加" : "已移除");
                    sb.Append($"「{defName}」→ 存储区 ({posX}, {posY})");
                    sb.Append($" | 当前允许 {allowedCount} 种物品");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"管理存储区筛选失败: {ex.Message}"); }
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
