using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 取消建造蓝图/框架和拆除/采矿等标记。
    /// 对应游戏内的"取消"工具（Designator_Cancel）。
    /// </summary>
    public class Tool_CancelBuild : ITool, IRequiresAdvanceTick
    {
        public string Name => "cancel_build";
        public string Description => "取消指定区域的建造蓝图、框架和各种标记（采矿/砍伐/收割/拆除等）。对应游戏内的\"取消\"工具。不提供范围则取消单格。坐标范围为闭区间（两端坐标均包含）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左下 X 坐标" },
                pos_y = new { type = "integer", description = "左下 Y 坐标" },
                end_x = new { type = "integer", description = "右上 X 坐标（可选，不提供则只取消单格）" },
                end_y = new { type = "integer", description = "右上 Y 坐标（可选，不提供则只取消单格）" },
                cancel_blueprints = new { type = "boolean", description = "是否取消建造蓝图（默认 true）", @default = true },
                cancel_frames = new { type = "boolean", description = "是否取消建造框架（默认 true）", @default = true },
                cancel_designations = new { type = "boolean", description = "是否取消标记（采矿/砍伐/收割/拆除等，默认 true）", @default = true }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var startX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var startY))
                return ToolResult.Error("缺少必填参数: pos_y");

            int endX = startX;
            int endY = startY;
            if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex))
                endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey))
                endY = ey;

            bool cancelBlueprints = true;
            bool cancelFrames = true;
            bool cancelDesignations = true;
            if (args.Value.TryGetProperty("cancel_blueprints", out var jCb) && jCb.ValueKind == JsonValueKind.False)
                cancelBlueprints = false;
            if (args.Value.TryGetProperty("cancel_frames", out var jCf) && jCf.ValueKind == JsonValueKind.False)
                cancelFrames = false;
            if (args.Value.TryGetProperty("cancel_designations", out var jCd) && jCd.ValueKind == JsonValueKind.False)
                cancelDesignations = false;

            if (!cancelBlueprints && !cancelFrames && !cancelDesignations)
                return ToolResult.Error("cancel_blueprints/cancel_frames/cancel_designations 不能同时为 false。");

            int minX = Math.Min(startX, endX);
            int maxX = Math.Max(startX, endX);
            int minZ = Math.Min(startY, endY);
            int maxZ = Math.Max(startY, endY);

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var area = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                    area.ClipInsideMap(map);

                    if (area.IsEmpty)
                        return ToolResult.Error($"指定范围 ({minX},{minZ})→({maxX},{maxZ}) 完全在地图外");

                    var cells = area.Cells.ToList();

                    // 全部取消走 Designator_Cancel 快速路径（含 MineVein 级联移除）
                    if (cancelBlueprints && cancelFrames && cancelDesignations)
                    {
                        var des = new Designator_Cancel();
                        des.DesignateMultiCell(cells);
                        return ToolResult.Success($"已取消区域 ({minX},{minZ})→({maxX},{maxZ}) 的全部建造项目和标记（含矿脉级联）。");
                    }

                    // 部分取消：手动分拣
                    int bpCount = 0, frCount = 0, desigCount = 0;
                    foreach (var cell in cells)
                    {
                        if (cell.Fogged(map)) continue;

                        if (cancelBlueprints || cancelFrames)
                        {
                            var things = cell.GetThingList(map);
                            for (int i = things.Count - 1; i >= 0; i--)
                            {
                                var t = things[i];
                                if (cancelBlueprints && t is Blueprint)
                                { t.Destroy(DestroyMode.Cancel); bpCount++; }
                                else if (cancelFrames && t is Frame)
                                { t.Destroy(DestroyMode.Cancel); frCount++; }
                            }
                        }

                        if (cancelDesignations)
                        {
                            foreach (var des in map.designationManager.AllDesignationsAt(cell).ToList())
                            {
                                if (des.def.designateCancelable)
                                { map.designationManager.RemoveDesignation(des); desigCount++; }
                            }
                        }
                    }

                    if (bpCount == 0 && frCount == 0 && desigCount == 0)
                        return ToolResult.Success($"区域 ({minX},{minZ})→({maxX},{maxZ}) 没有需要取消的建造项目或标记。");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 取消结果 ({minX},{minZ})→({maxX},{maxZ})");
                    if (bpCount > 0) sb.AppendLine($"- 取消蓝图: {bpCount} 处");
                    if (frCount > 0) sb.AppendLine($"- 取消框架: {frCount} 处");
                    if (desigCount > 0) sb.AppendLine($"- 取消标记: {desigCount} 处");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"取消建造失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var x)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var y)) return null;
            int ex = x, ey = y;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var _ex)) ex = _ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var _ey)) ey = _ey;
            return (Math.Min(x, ex), Math.Min(y, ey), Math.Max(x, ex), Math.Max(y, ey));
        }
}
}
