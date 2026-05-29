using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 在指定坐标对指定殖民者生成右键菜单（FloatMenu），列出所有可用操作。
    /// 选中后使用 select_right_click 执行。
    /// </summary>
    public class Tool_RightClickMenu : ITool
    {
        public string Name => "get_right_click_menu";
        public string Description => "对指定物品/坐标生成右键菜单，列出该殖民者可执行的所有操作（如植入、应用蓝图、优先搬运等）。传 thing_id 则查看该物品能做什么，传 pos_x/pos_y 则查看该坐标能做什么。选中后用 select_right_click 执行。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists），右键执行者" },
                thing_id = new { type = "integer", description = "目标物品/建筑 ID（来自 get_tile_detail）。与 pos_x/pos_y 二选一" },
                pos_x = new { type = "integer", description = "点击目标 X 坐标（与 thing_id 二选一）" },
                pos_y = new { type = "integer", description = "点击目标 Y 坐标（与 thing_id 二选一）" }
            },
            required = new[] { "colonist_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少 colonist_id");

            int thingId = 0;
            bool hasThingId = false;
            if (args.Value.TryGetProperty("thing_id", out var jTid) && jTid.TryGetInt32(out var tid))
            { hasThingId = true; thingId = tid; }

            int posX = 0, posY = 0;
            bool hasPos = false;
            if (args.Value.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var px)
                && args.Value.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var py))
            { hasPos = true; posX = px; posY = py; }

            if (!hasThingId && !hasPos)
                return ToolResult.Error("必须提供 thing_id 或 (pos_x + pos_y)");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null) return ToolResult.Error($"未找到殖民者 ID={colonistId}");

                    IntVec3 cell;
                    Thing? targetThing = null;
                    string targetLabel;

                    if (hasThingId)
                    {
                        targetThing = map.listerThings.AllThings
                            .FirstOrDefault(t => t.thingIDNumber == thingId);
                        if (targetThing == null)
                            return ToolResult.Error($"未找到物品 ID={thingId}");
                        cell = targetThing.Position;
                        targetLabel = $"{targetThing.Label} ({cell.x},{cell.z})";
                    }
                    else
                    {
                        cell = new IntVec3(posX, 0, posY);
                        if (!cell.InBounds(map)) return ToolResult.Error($"坐标 ({posX},{posY}) 超出地图范围");
                        targetLabel = $"({cell.x},{cell.z})";
                    }

                    // 调用游戏原生右键菜单生成
                    var selectedPawns = new List<Pawn> { pawn };
                    var clickPos = cell.ToVector3Shifted();
                    var options = FloatMenuMakerMap.GetOptions(selectedPawns, clickPos, out var context);

                    if (options == null || options.Count == 0)
                        return ToolResult.Success($"{targetLabel} 没有 {pawn.LabelShort} 可用的右键操作。");

                    // 存储供后续选择
                    RightClickMenuStore.Store(options, cell, pawn);

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 右键菜单 {targetLabel} — {pawn.LabelShort}");
                    sb.AppendLine();

                    for (int i = 0; i < options.Count; i++)
                    {
                        var opt = options[i];
                        if (opt.Disabled)
                            sb.AppendLine($"[{i}] {opt.Label} [禁用]");
                        else
                            sb.AppendLine($"[{i}] {opt.Label}");
                    }

                    sb.AppendLine();
                    sb.Append($"共 {options.Count} 项，使用 select_right_click(option_index=N) 执行");
                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"右键菜单生成失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (args.Value.TryGetProperty("pos_x", out var jx) && jx.TryGetInt32(out var x)
                && args.Value.TryGetProperty("pos_y", out var jy) && jy.TryGetInt32(out var y))
                return (x, y, x, y);
            return null;
        }
    }

    /// <summary>存储最近一次右键菜单选项，供 select_right_click 选择</summary>
    public static class RightClickMenuStore
    {
        private static List<FloatMenuOption>? _options;
        private static IntVec3 _cell;
        private static Pawn? _pawn;

        public static void Store(List<FloatMenuOption> options, IntVec3 cell, Pawn pawn)
        {
            _options = options;
            _cell = cell;
            _pawn = pawn;
        }

        public static bool TrySelect(int index, out string result)
        {
            result = "";
            if (_options == null || index < 0 || index >= _options.Count)
            {
                result = $"选项 {index} 无效（可用 0~{(_options?.Count ?? 0) - 1}）";
                return false;
            }

            var opt = _options[index];
            if (opt.Disabled)
            {
                result = $"选项 [{index}] {opt.Label} 已禁用";
                return false;
            }

            try
            {
                opt.Chosen(true, null);
                result = $"已执行: {opt.Label}";
                _options = null; // 用完清除
                return true;
            }
            catch (Exception ex)
            {
                result = $"执行失败: {ex.Message}";
                return false;
            }
        }
    }
}
