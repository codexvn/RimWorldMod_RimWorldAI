using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 列出地图上未完成的建造目标：蓝图(Blueprint)与框架(Frame)。
    /// 解决 AI 无法回忆“自己刚指定了什么、建到哪了”的问题；与 plan_list（仅色块规划）互补。
    /// </summary>
    public class Tool_ListConstruction : ITool
    {
        public string Name => "list_construction";
        public string Description =>
            "列出地图上未完成的建造：蓝图(待搬运/待开工)与框架(建造中)。" +
            "返回 defName、材料、坐标、朝向、进度。用于核对「计划/已指定/在建」状态。" +
            "注意：plan_list 只显示色块规划区，不包含具体建筑蓝图；查具体建造进度请用本工具。" +
            "已建成建筑用 get_structure_layout / list_devices。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                status = new
                {
                    type = "string",
                    description = "过滤状态：all=蓝图+框架(默认)，blueprint=仅蓝图，frame=仅框架",
                    @enum = new[] { "all", "blueprint", "frame" },
                    @default = "all"
                },
                keyword = new { type = "string", description = "模糊匹配 Label 或 defName（如 床、Wall、PowerConduit）" },
                defName = new { type = "string", description = "精确匹配将要建成的 entity defName（如 Wall、Bed、PowerConduit）" },
                pos_x = new { type = "integer", description = "查询范围左下 X（可选，与 pos_y 一起表示范围）" },
                pos_y = new { type = "integer", description = "查询范围左下 Y" },
                end_x = new { type = "integer", description = "查询范围右上 X（可选）" },
                end_y = new { type = "integer", description = "查询范围右上 Y（可选）" },
                max_items = new { type = "integer", description = "最多返回条数（默认 80，最大 200）", @default = 80 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string status = "all";
            if (args != null && args.Value.TryGetProperty("status", out var jSt) && jSt.ValueKind == JsonValueKind.String)
                status = (jSt.GetString() ?? "all").Trim().ToLowerInvariant();
            if (status != "all" && status != "blueprint" && status != "frame")
                return ToolResult.Error("status 可选: all, blueprint, frame");

            string keyword = "";
            if (args != null && args.Value.TryGetProperty("keyword", out var jKw))
                keyword = jKw.GetString() ?? "";

            string defName = "";
            if (args != null && args.Value.TryGetProperty("defName", out var jDn))
                defName = jDn.GetString() ?? "";

            int posX = 0, posY = 0, endX = 0, endY = 0;
            bool hasRange = false;
            if (args != null
                && args.Value.TryGetProperty("pos_x", out var jX)
                && args.Value.TryGetProperty("pos_y", out var jY))
            {
                if (!jX.TryGetInt32(out posX)) return ToolResult.Error("pos_x 需要整数");
                if (!jY.TryGetInt32(out posY)) return ToolResult.Error("pos_y 需要整数");
                endX = posX;
                endY = posY;
                if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)) endX = ex;
                if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey)) endY = ey;
                hasRange = true;
            }

            int maxItems = 80;
            if (args != null && args.Value.TryGetProperty("max_items", out var jMax) && jMax.TryGetInt32(out var mi))
                maxItems = Math.Max(1, Math.Min(200, mi));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载存档。");

                    CellRect? area = null;
                    if (hasRange)
                    {
                        int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
                        int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);
                        var rect = CellRect.FromLimits(minX, minZ, maxX, maxZ);
                        rect.ClipInsideMap(map);
                        area = rect;
                    }

                    var rows = new List<Row>();

                    if (status == "all" || status == "blueprint")
                    {
                        foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
                        {
                            if (t is not Blueprint_Build bp) continue;
                            if (area.HasValue && !area.Value.Contains(bp.Position)) continue;
                            if (!MatchFilter(bp.def.entityDefToBuild, bp.Label, keyword, defName)) continue;
                            rows.Add(Row.FromBlueprint(bp));
                        }
                    }

                    if (status == "all" || status == "frame")
                    {
                        foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
                        {
                            if (t is not Frame frame) continue;
                            if (area.HasValue && !area.Value.Contains(frame.Position)) continue;
                            if (!MatchFilter(frame.def.entityDefToBuild, frame.Label, keyword, defName)) continue;
                            rows.Add(Row.FromFrame(frame));
                        }
                    }

                    rows = rows
                        .OrderBy(r => r.StatusRank)
                        .ThenBy(r => r.EntityDef)
                        .ThenBy(r => r.X)
                        .ThenBy(r => r.Z)
                        .ToList();

                    if (rows.Count == 0)
                    {
                        var scope = area.HasValue
                            ? $"范围 ({area.Value.minX},{area.Value.minZ})~({area.Value.maxX},{area.Value.maxZ})"
                            : "全图";
                        return ToolResult.Success($"## 建造进度\n{scope}内无未完成的蓝图/框架（status={status}）。\n" +
                            "提示：plan_list 只显示色块规划区；已建成建筑请用 get_structure_layout / list_devices。");
                    }

                    int total = rows.Count;
                    bool truncated = total > maxItems;
                    if (truncated) rows = rows.Take(maxItems).ToList();

                    int bpCount = rows.Count(r => r.Status == "蓝图");
                    int frCount = rows.Count(r => r.Status == "框架");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 建造进度（显示 {rows.Count}/{total}，蓝图 {bpCount} / 框架 {frCount}）");
                    if (area.HasValue)
                        sb.AppendLine($"> 范围 ({area.Value.minX},{area.Value.minZ})~({area.Value.maxX},{area.Value.maxZ})");
                    sb.AppendLine();
                    sb.AppendLine("| 状态 | defName | 材料 | 位置 | 朝向 | 进度 | 标签 |");
                    sb.AppendLine("|------|---------|------|------|------|------|------|");

                    foreach (var r in rows)
                    {
                        sb.AppendLine($"| {r.Status} | {r.EntityDef} | {r.Stuff} | ({r.X},{r.Z}) | {r.Rotation} | {r.Progress} | {r.Label} |");
                    }

                    if (truncated)
                        sb.AppendLine($"> 已截断，共 {total} 条。可缩小范围、加 keyword/defName，或提高 max_items。");

                    // 按 def 汇总，帮助 AI 快速知道「计划里有什么」
                    sb.AppendLine();
                    sb.AppendLine("### 按 def 汇总");
                    foreach (var g in rows.GroupBy(r => r.EntityDef).OrderByDescending(g => g.Count()))
                    {
                        int b = g.Count(x => x.Status == "蓝图");
                        int f = g.Count(x => x.Status == "框架");
                        sb.AppendLine($"- {g.Key}: {g.Count()}（蓝图 {b}，框架 {f}）");
                    }

                    sb.AppendLine();
                    sb.AppendLine("说明：蓝图=已指定未完成框架；框架=已开工建造中。plan_list 色块≠本表。");
                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"list_construction 失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey)) endY = ey;
            return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
        }

        private static bool MatchFilter(BuildableDef? entity, string label, string keyword, string defName)
        {
            var entityName = entity?.defName ?? "";
            if (!string.IsNullOrEmpty(defName)
                && !entityName.Equals(defName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrEmpty(keyword)) return true;
            if (entityName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrEmpty(label) && label.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            var entityLabel = entity?.label ?? "";
            if (entityLabel.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private sealed class Row
        {
            public string Status = "";
            public int StatusRank;
            public string EntityDef = "";
            public string Stuff = "-";
            public int X;
            public int Z;
            public string Rotation = "N";
            public string Progress = "-";
            public string Label = "";

            public static Row FromBlueprint(Blueprint_Build bp)
            {
                var entity = bp.def.entityDefToBuild;
                return new Row
                {
                    Status = "蓝图",
                    StatusRank = 0,
                    EntityDef = entity?.defName ?? bp.def.defName,
                    Stuff = bp.stuffToUse?.defName ?? "-",
                    X = bp.Position.x,
                    Z = bp.Position.z,
                    Rotation = RotLabel(bp.Rotation),
                    Progress = "0%",
                    Label = Sanitize(bp.Label)
                };
            }

            public static Row FromFrame(Frame frame)
            {
                var entity = frame.def.entityDefToBuild;
                var pct = Math.Max(0, Math.Min(100, (int)Math.Round(frame.PercentComplete * 100f)));
                return new Row
                {
                    Status = "框架",
                    StatusRank = 1,
                    EntityDef = entity?.defName ?? frame.def.defName,
                    Stuff = frame.Stuff?.defName ?? frame.EntityToBuildStuff()?.defName ?? "-",
                    X = frame.Position.x,
                    Z = frame.Position.z,
                    Rotation = RotLabel(frame.Rotation),
                    Progress = $"{pct}%",
                    Label = Sanitize(frame.Label)
                };
            }

            private static string RotLabel(Rot4 rot)
            {
                if (rot == Rot4.North) return "N";
                if (rot == Rot4.East) return "E";
                if (rot == Rot4.South) return "S";
                if (rot == Rot4.West) return "W";
                return rot.ToStringHuman();
            }

            private static string Sanitize(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                return s.Replace("|", "/").Replace("\n", " ").Replace("\r", "");
            }
        }
    }
}
