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
    public class Tool_QueryStockpileFilter : ITool
    {
        public string Name => "query_stockpile_filter";
        public string Description => "查询存储区的筛选规则，以树形结构展示允许/禁止的物品分类。支持按需展开层级或搜索过滤。同时显示所有可用优先级和当前优先级。路径段支持中文 label 和 defName。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "存储区范围内任意格 X 坐标" },
                pos_y = new { type = "integer", description = "存储区范围内任意格 Y 坐标" },
                mode = new
                {
                    type = "string",
                    description = "查询模式: expand（按需展开）或 search（搜索过滤）",
                    @enum = new[] { "expand", "search" },
                    @default = "expand"
                },
                items = new
                {
                    type = "array",
                    description = "mode=expand: 要展开的节点路径列表。用 . 分隔层级，支持中文 label 和 defName。如 [\"食物.肉类\", \"制成品\"]",
                    items = new { type = "string" }
                },
                search = new
                {
                    type = "string",
                    description = "mode=search: 搜索关键词，中文/英文均可，大小写不敏感"
                }
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

            string mode = "expand";
            if (args.Value.TryGetProperty("mode", out var jMode))
                mode = jMode.GetString() ?? "expand";

            var expandPaths = new HashSet<string>();
            if (args.Value.TryGetProperty("items", out var jItems) && jItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in jItems.EnumerateArray())
                {
                    var path = item.GetString();
                    if (!string.IsNullOrEmpty(path))
                        expandPaths.Add(path);
                }
            }

            string searchText = "";
            if (args.Value.TryGetProperty("search", out var jSearch))
                searchText = jSearch.GetString() ?? "";

            var expandSet = expandPaths;
            var searchLower = searchText.ToLowerInvariant();

            return await Task.Run(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    var cell = new IntVec3(posX, 0, posY);
                    var zone = map.zoneManager.ZoneAt(cell) as Zone_Stockpile;
                    if (zone == null)
                        return ToolResult.Error($"({posX}, {posY}) 处没有存储区。请使用 get_structure_layout 查看存储区位置。");

                    var settings = zone.settings;
                    var filter = settings.filter;
                    var root = ThingCategoryDefOf.Root;
                    if (root == null)
                        return ToolResult.Error("ThingCategory 根节点未初始化");

                    var sb = new StringBuilder();

                    // ===== 顶部：基本信息 =====
                    var cells = zone.Cells.ToList();
                    int minX = cells.Min(c => c.x), maxX = cells.Max(c => c.x);
                    int minZ = cells.Min(c => c.z), maxZ = cells.Max(c => c.z);
                    sb.AppendLine($"储存区 ({minX},{minZ})-({maxX},{maxZ})");

                    // ===== 优先级区 =====
                    sb.Append("全部优先级: ");
                    var allPriorities = new[] { StoragePriority.Low, StoragePriority.Normal, StoragePriority.Preferred, StoragePriority.Important, StoragePriority.Critical };
                    var prioLabels = allPriorities.Select(p =>
                    {
                        var label = PriorityLabel(p);
                        return p == settings.Priority ? $"{label} ★" : label;
                    });
                    sb.AppendLine(string.Join(" | ", prioLabels));
                    sb.AppendLine($"当前优先级: {PriorityLabel(settings.Priority)} ★");
                    sb.AppendLine("──────────────────────────");

                    // ===== 树形结构 =====
                    bool isSearch = mode == "search" && !string.IsNullOrEmpty(searchText);
                    RenderCategoryTree(sb, root, filter, "", expandSet, searchLower, isSearch);

                    // ===== 直接挂在根层的 ThingDef（未被分类覆盖的） =====
                    if (!isSearch)
                    {
                        foreach (var def in root.childThingDefs)
                        {
                            AppendThingDef(sb, def, filter, "");
                        }
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"查询失败: {ex.Message}");
                }
            });
        }

        // ===== 树形渲染 =====

        private static void RenderCategoryTree(StringBuilder sb, ThingCategoryDef cat, ThingFilter filter,
            string indent, HashSet<string> expandSet, string searchLower, bool isSearch)
        {
            foreach (var childCat in cat.childCategories)
            {
                var childIndent = indent;
                bool expanded = IsExpanded(childCat, expandSet);
                bool matchesSearch = isSearch && MatchesSearch(childCat, searchLower);
                bool descendantMatches = isSearch && DescendantMatchesSearch(childCat, searchLower);

                // 搜索模式：不匹配且无后代匹配则跳过
                if (isSearch && !matchesSearch && !descendantMatches)
                    continue;

                // 搜索模式：有后代匹配但不匹配自身时仍然显示（作为祖先路径），但折叠
                bool forceExpand = isSearch && descendantMatches;

                var state = StateOf(childCat, filter);
                var checkbox = state == MultiCheckboxState.On ? "✔" : state == MultiCheckboxState.Partial ? "◐" : "✘";
                var arrow = (expanded || forceExpand) ? "▼" : "▶";
                sb.AppendLine($"{indent}{arrow} {childCat.label} {checkbox}");

                if (expanded || forceExpand)
                {
                    // 先渲染直接子物品
                    foreach (var def in childCat.childThingDefs)
                    {
                        if (isSearch && !ThingMatchesSearch(def, searchLower)) continue;
                        AppendThingDef(sb, def, filter, indent + "│  ");
                    }
                    // 再递归子分类
                    RenderCategoryTree(sb, childCat, filter, indent + "│  ", expandSet, searchLower, isSearch);
                }
            }
        }

        private static void AppendThingDef(StringBuilder sb, ThingDef def, ThingFilter filter, string indent)
        {
            var allowed = filter.Allows(def);
            sb.AppendLine($"{indent}{def.label} {(allowed ? "✔" : "✘")}");
        }

        private static void AppendSpecialFilter(StringBuilder sb, string label, bool allowed)
        {
            sb.AppendLine($"*{label} {(allowed ? "✔" : "✘")}");
        }

        // ===== 勾选状态 =====

        private static MultiCheckboxState StateOf(ThingCategoryDef cat, ThingFilter filter)
        {
            int total = 0, allowed = 0;
            foreach (var def in cat.DescendantThingDefs)
            {
                total++;
                if (filter.Allows(def)) allowed++;
            }
            if (total == 0) return MultiCheckboxState.Off;
            if (allowed == 0) return MultiCheckboxState.Off;
            if (allowed == total) return MultiCheckboxState.On;
            return MultiCheckboxState.Partial;
        }

        // ===== 展开控制 =====

        private static bool IsExpanded(ThingCategoryDef cat, HashSet<string> expandSet)
        {
            foreach (var path in expandSet)
            {
                if (PathMatches(cat, path))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 检查分类是否匹配路径（支持 "食物.肉类" 中文路径 或 "Foods.Meat" defName 路径）
        /// </summary>
        private static bool PathMatches(ThingCategoryDef cat, string path)
        {
            var segments = path.Split('.');
            if (segments.Length == 0) return false;

            // 从后往前匹配：路径最后一节必须匹配当前分类
            var last = segments[segments.Length - 1];
            if (!SegmentMatches(cat, last)) return false;

            if (segments.Length == 1) return true;

            // 向前验证祖先链
            var ancestors = cat.Parents.ToList();
            ancestors.Reverse(); // 从根到父
            for (int i = segments.Length - 2; i >= 0; i--)
            {
                int ancestorIdx = segments.Length - 2 - i;
                if (ancestorIdx >= ancestors.Count) return false;
                if (!SegmentMatches(ancestors[ancestorIdx], segments[i])) return false;
            }
            return segments.Length - 1 <= ancestors.Count;
        }

        private static bool SegmentMatches(ThingCategoryDef cat, string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            return string.Equals(cat.defName, segment, StringComparison.OrdinalIgnoreCase)
                || string.Equals(cat.label, segment, StringComparison.OrdinalIgnoreCase)
                || cat.label?.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ===== 搜索 =====

        private static bool MatchesSearch(ThingCategoryDef cat, string searchLower)
        {
            return cat.label?.ToLowerInvariant().Contains(searchLower) == true
                || cat.defName.ToLowerInvariant().Contains(searchLower);
        }

        private static bool ThingMatchesSearch(ThingDef def, string searchLower)
        {
            return def.label?.ToLowerInvariant().Contains(searchLower) == true
                || def.defName.ToLowerInvariant().Contains(searchLower);
        }

        private static bool DescendantMatchesSearch(ThingCategoryDef cat, string searchLower)
        {
            foreach (var def in cat.DescendantThingDefs)
                if (ThingMatchesSearch(def, searchLower))
                    return true;
            foreach (var childCat in cat.childCategories)
                if (MatchesSearch(childCat, searchLower) || DescendantMatchesSearch(childCat, searchLower))
                    return true;
            return false;
        }

        // ===== 优先级标签 =====

        private static string PriorityLabel(StoragePriority p) => p switch
        {
            StoragePriority.Low => "低",
            StoragePriority.Normal => "普通",
            StoragePriority.Preferred => "优选",
            StoragePriority.Important => "重要",
            StoragePriority.Critical => "关键",
            _ => "?"
        };

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            return (posX, posY, posX, posY);
        }
    }
}
