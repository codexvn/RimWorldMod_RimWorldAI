using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_UninstallBuilding : ITool
    {
        public string Name => "uninstall_building";
        public string Description => "拆卸指定建筑为微缩物品。通过 thing_id 定位建筑，添加拆卸工作标记由殖民者执行，或即时拆卸零工作建筑/Frame。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "目标建筑的唯一 ID（来自 get_tile_detail）" }
            },
            required = new[] { "thing_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("thing_id", out var jTid) || !jTid.TryGetInt32(out var thingId))
                return ToolResult.Error("缺少必填参数: thing_id");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    Thing? thing = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == thingId);
                    if (thing == null)
                        return ToolResult.Error($"找不到 ID={thingId} 的物品。");

                    if (!(thing is Building))
                        return ToolResult.Error($"{thing.Label} ({thing.def.defName}) 不是建筑，无法拆卸。");

                    var designator = new Designator_Uninstall();
                    var canDesignate = designator.CanDesignateThing(thing);
                    if (!canDesignate.Accepted)
                        return ToolResult.Error($"无法拆卸 {thing.Label}：{canDesignate.Reason}");

                    designator.DesignateThing(thing);

                    // 检查是否已立即完成（god mode / 零工作 / Frame）
                    if (thing.Destroyed || !thing.Spawned)
                        return ToolResult.Success($"{thing.Label} 已立即拆卸完成。");

                    return ToolResult.Success($"已标记 {thing.Label} 待拆卸，殖民者将前往执行。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"拆卸失败: {ex.Message}");
                }
            });
        }
    }
}
