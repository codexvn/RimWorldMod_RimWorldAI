using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetColonistNeeds : ITool
    {
        public string Name => "get_colonist_needs";
        public string Description => "获取殖民者的详细需求状态：心情、食物、休息、娱乐、舒适、美观、户外等各项需求的当前百分比。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists），不传返回全部" },
                colonist_name = new { type = "string", description = "殖民者名称（模糊匹配），不传返回全部" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            int? colonistId = null;
            string nameFilter = "";
            if (args != null)
            {
                if (args.Value.TryGetProperty("colonist_id", out var jId) && jId.TryGetInt32(out var cid))
                    colonistId = cid;
                if (args.Value.TryGetProperty("colonist_name", out var n))
                    nameFilter = n.GetString() ?? "";
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                if (colonists == null || colonists.Count == 0)
                    return ToolResult.Success("## 殖民者需求状态\n\n暂无自由殖民者。");

                IEnumerable<Pawn> filtered = colonists;
                if (colonistId.HasValue)
                    filtered = colonists.Where(c => c.thingIDNumber == colonistId.Value);
                else if (!string.IsNullOrEmpty(nameFilter))
                    filtered = colonists.Where(c =>
                        c.Name.ToStringShort.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        c.Name.ToStringFull.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                var items = filtered.OrderBy(p => p.thingIDNumber).ToList();
                if (items.Count == 0)
                    return ToolResult.Success("没有匹配的殖民者。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 殖民者需求状态 ({items.Count} 人)");

                foreach (var pawn in items)
                {
                    string name = pawn.Name.ToStringShort;
                    sb.AppendLine();
                    sb.AppendLine($"### {name}");

                    var needs = pawn.needs?.AllNeeds;
                    if (needs == null || needs.Count == 0)
                    {
                        sb.AppendLine("- 无需求数据");
                        continue;
                    }

                    var issues = new List<string>();

                    foreach (var need in needs)
                    {
                        if (need == null) continue;

                        try
                        {
                            float curLevel = need.CurLevelPercentage;
                            string label = need.def?.LabelCap ?? need.LabelCap;
                            string bar = BuildBar(curLevel);
                            int pct = (int)(curLevel * 100);
                            sb.AppendLine($"- {label} {bar} {pct}%");

                            // 如果是心情需求，显示想法详情
                            if (need is Need_Mood needMood && needMood.thoughts != null)
                            {
                                try
                                {
                                    var allMoodThoughts = new List<Thought>();
                                    needMood.thoughts.GetAllMoodThoughts(allMoodThoughts);
                                    if (allMoodThoughts.Count > 0)
                                    {
                                        var top3 = allMoodThoughts
                                            .OrderByDescending(t => Math.Abs(t.MoodOffset()))
                                            .Take(3)
                                            .Select(t => $"{t.LabelCap}{(int)t.MoodOffset():+#;-#;0}");
                                        sb.AppendLine($"    想法: {string.Join(", ", top3)}");
                                    }
                                }
                                catch (Exception) { }
                            }

                            // 标记过低的项
                            if (curLevel < 0.25f)
                                issues.Add($"{label}极低({pct}%)");
                            else if (curLevel < 0.40f)
                                issues.Add($"{label}偏低({pct}%)");
                        }
                        catch (Exception)
                        {
                            try
                            {
                                sb.AppendLine($"- {need.def?.LabelCap ?? "未知需求"}: 数据不可用");
                            }
                            catch (Exception) { }
                        }
                    }

                    if (issues.Count > 0)
                    {
                        sb.AppendLine($"- ⚠ 问题: {string.Join(", ", issues)}");
                    }
                }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string BuildBar(float pct)
        {
            // 生成一个 10 段的进度条
            int filled = (int)Math.Round(pct * 10);
            filled = Math.Max(0, Math.Min(10, filled));
            string bar = new string('#', filled) + new string('_', 10 - filled);
            return $"[{bar}]";
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
