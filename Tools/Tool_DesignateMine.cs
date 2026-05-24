using System;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateMine : ITool
    {
        public string Name => "designate_mine";
        public string Description => "标记指定位置的岩石/矿物以供开采。殖民者会在工作完成后执行采矿。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "X 坐标" },
                pos_y = new { type = "integer", description = "Y 坐标" },
                pos_z = new { type = "integer", description = "Z 坐标" }
            },
            required = new[] { "pos_x", "pos_y", "pos_z" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");
            if (!args.Value.TryGetProperty("pos_z", out var jZ) || !jZ.TryGetInt32(out var posZ))
                return ToolResult.Error("缺少必填参数: pos_z");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    IntVec3 pos = new IntVec3(posX, posY, posZ);
                    if (!pos.InBounds(map))
                        return ToolResult.Error($"坐标 ({posX}, {posY}, {posZ}) 超出地图边界。");

                    if (map.designationManager.DesignationAt(pos, DesignationDefOf.Mine) != null)
                    {
                        Mineable existing = pos.GetFirstMineable(map);
                        string label = existing?.def.label ?? "未知";
                        return ToolResult.Success($"坐标 ({posX}, {posY}, {posZ}) 已被标记为开采（{label}），无需重复操作。");
                    }

                    if (!pos.Fogged(map))
                    {
                        Mineable mineable = pos.GetFirstMineable(map);
                        if (mineable == null)
                            return ToolResult.Error($"坐标 ({posX}, {posY}, {posZ}) 没有可开采的矿物或岩石。");
                    }

                    map.designationManager.AddDesignation(new Designation(pos, DesignationDefOf.Mine, null));
                    map.designationManager.TryRemoveDesignation(pos, DesignationDefOf.SmoothWall);
                    if (DesignationDefOf.MineVein != null)
                        map.designationManager.TryRemoveDesignation(pos, DesignationDefOf.MineVein);

                    Mineable mined = pos.GetFirstMineable(map);
                    string minedLabel = mined?.def.label ?? "岩石";
                    string? productInfo = mined?.def.building?.mineableThing?.label;
                    string extraInfo = productInfo != null ? $"，预期产出: {productInfo}" : "";

                    return ToolResult.Success($"已标记 {minedLabel} 在坐标 ({posX}, {posY}, {posZ}) 以供开采{extraInfo}。");
                }
                catch (Exception ex) { return ToolResult.Error($"标记采矿失败: {ex.Message}"); }
            });
        }
    }
}
