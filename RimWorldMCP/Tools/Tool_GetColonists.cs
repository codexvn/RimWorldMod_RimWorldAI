using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetColonists : ITool
    {
        public string Name => "get_colonists";
        public string Description => "获取所有殖民者的详细信息，包括技能等级、心情、健康状态、当前装备和工作任务。可按名称过滤。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { colonist_name = new { type = "string", description = "殖民者名称（模糊匹配），不传则返回全部" } }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string nameFilter = "";
            if (args != null && args.Value.TryGetProperty("colonist_name", out var n))
                nameFilter = n.GetString() ?? "";

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                if (colonists == null || colonists.Count == 0)
                    return ToolResult.Success("## 殖民者\n\n暂无自由殖民者。");

                // 按名称过滤
                IEnumerable<Pawn> filtered = colonists;
                if (!string.IsNullOrEmpty(nameFilter))
                    filtered = colonists.Where(c => c.Name.ToStringShort.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0
                        || c.Name.ToStringFull.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                var items = filtered.OrderBy(p => p.thingIDNumber).ToList();
                if (items.Count == 0)
                    return ToolResult.Success("没有匹配的殖民者。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 殖民者 ({items.Count} 人)");

                foreach (var pawn in items)
                {
                    string name = pawn.Name.ToStringShort;
                    int age = pawn.ageTracker?.AgeBiologicalYears ?? 0;
                    string gender = pawn.gender switch
                    {
                        Gender.Male => "男",
                        Gender.Female => "女",
                        _ => "?"
                    };

                    // 心情
                    float moodPct = pawn.needs?.mood?.CurLevelPercentage ?? -1f;
                    string moodLabel = GetMoodLabel(moodPct);

                    // 健康摘要
                    string healthSummary = GetHealthSummary(pawn);

                    // 特性
                    string traitsStr = GetTraits(pawn);

                    // 技能 Top 3
                    string skillsStr = GetTopSkills(pawn);

                    // 装备
                    string equipmentStr = GetEquipmentSummary(pawn);

                    // 当前活动
                    string currentActivity = GetCurrentActivity(pawn);

                    // 工作优先级
                    string workPriorities = GetWorkPriorities(pawn);

                    // 意识形态角色和头衔
                    string ideoAndTitle = GetIdeoAndTitle(pawn);

                    // 逃跑检测
                    bool isFleeing = IsFleeing(pawn);

                    // 精神态
                    string mentalStateStr = GetMentalState(pawn);

                    // 最近日志
                    string recentLogs = GetRecentLogs(pawn);

                    string statusTag = isFleeing ? " **逃跑中!**" : (pawn.Downed ? " **倒地**" : "");

                    sb.AppendLine();
                    sb.AppendLine($"### {name} (ID:{pawn.thingIDNumber}){statusTag}");
                    sb.AppendLine($"- {name} ({age}岁, {gender}) | 心情: {moodLabel} | 健康: {healthSummary}");
                    sb.AppendLine($"  特性: {traitsStr}");
                    sb.AppendLine($"  技能: {skillsStr}");
                    sb.AppendLine($"  装备: {equipmentStr}");
                    sb.AppendLine($"  当前: {currentActivity} | 工作: {workPriorities}");
                    if (!string.IsNullOrEmpty(ideoAndTitle))
                        sb.AppendLine($"  {ideoAndTitle}");
                    if (!string.IsNullOrEmpty(mentalStateStr))
                        sb.AppendLine($"  精神态: {mentalStateStr}");
                    if (!string.IsNullOrEmpty(recentLogs))
                        sb.AppendLine($"  最近日志: {recentLogs}");
                }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string GetMoodLabel(float pct)
        {
            if (pct < 0) return "无";
            if (pct >= 0.85f) return "非常愉快";
            if (pct >= 0.65f) return "满意";
            if (pct >= 0.40f) return "一般";
            if (pct >= 0.15f) return "低落";
            return "崩溃边缘";
        }

        private static string GetHealthSummary(Pawn pawn)
        {
            try
            {
                var hediffs = pawn.health?.hediffSet?.hediffs;
                if (hediffs == null || hediffs.Count == 0) return "健康";

                var injuries = new List<string>();
                foreach (var h in hediffs)
                {
                    if (!h.Visible) continue;
                    if (h.def.defName == "Anesthetic" || h.def.defName == "Sedated") continue;

                    string part = h.Part?.Label ?? "";
                    string severity = h.Severity > 0.01f ? $" ({h.Severity * 100:F0}%)" : "";
                    string line;
                    if (!string.IsNullOrEmpty(part))
                        line = $"{h.Label}{severity}({part})";
                    else
                        line = $"{h.Label}{severity}";
                    injuries.Add(line);
                }

                return injuries.Count > 0 ? string.Join("; ", injuries) : "健康";
            }
            catch (Exception) { return "无法读取"; }
        }

        private static string GetTopSkills(Pawn pawn)
        {
            try
            {
                var skills = pawn.skills?.skills;
                if (skills == null || skills.Count == 0) return "无技能";

                var top3 = skills
                    .Where(s => s.Level > 0)
                    .OrderByDescending(s => s.Level)
                    .Take(3)
                    .Select(s =>
                    {
                        string passion = s.passion switch
                        {
                            Passion.Major => "**",
                            Passion.Minor => "*",
                            _ => ""
                        };
                        return $"{s.def.label}{s.Level}{passion}";
                    });

                return string.Join(" | ", top3);
            }
            catch (Exception) { return "无法读取"; }
        }

        private static string GetEquipmentSummary(Pawn pawn)
        {
            try
            {
                var parts = new List<string>();

                // 武器
                var weapon = pawn.equipment?.Primary;
                if (weapon != null)
                {
                    string weaponLabel = weapon.Label;
                    parts.Add(weaponLabel);
                }

                // 护甲（简化：列出主要装备）
                var apparel = pawn.apparel?.WornApparel;
                if (apparel != null)
                {
                    foreach (var a in apparel.Take(4))
                    {
                        parts.Add(a.Label);
                    }
                }

                return parts.Count > 0 ? string.Join(" + ", parts) : "无装备";
            }
            catch (Exception) { return "无法读取"; }
        }

        private static string GetWorkPriorities(Pawn pawn)
        {
            try
            {
                var workSettings = pawn.workSettings;
                if (workSettings == null) return "无";

                var activePriorities = new List<string>();
                var allWorkTypes = DefDatabase<WorkTypeDef>.AllDefsListForReading;
                foreach (var wt in allWorkTypes)
                {
                    try
                    {
                        int priority = workSettings.GetPriority(wt);
                        if (priority > 0)
                            activePriorities.Add($"{wt.labelShort}:{priority}");
                    }
                    catch (Exception) { }
                }

                var top = activePriorities.OrderBy(p => int.Parse(p.Split(':').Last())).Take(6);
                return activePriorities.Count > 0 ? string.Join(" ", top) : "未设置";
            }
            catch (Exception) { return "无法读取"; }
        }

        private static string GetTraits(Pawn pawn)
        {
            try
            {
                var allTraits = pawn.story?.traits?.allTraits;
                if (allTraits == null || allTraits.Count == 0) return "无";
                var labels = allTraits.Where(t => !t.Suppressed).Select(t => t.Label);
                var list = labels.ToList();
                return list.Count > 0 ? string.Join(", ", list) : "无";
            }
            catch (Exception) { return "无法读取"; }
        }

        private static string GetCurrentActivity(Pawn pawn)
        {
            try
            {
                var curJob = pawn.CurJob;
                int queued = pawn.jobs?.jobQueue?.Count ?? 0;
                string jobLabel = curJob?.def?.label ?? "";
                string queueSuffix = queued > 0 ? $" (排队:{queued})" : "";
                if (string.IsNullOrEmpty(jobLabel)) return $"空闲{queueSuffix}";
                return $"{jobLabel}{queueSuffix}";
            }
            catch (Exception) { return "空闲"; }
        }

        private static string GetIdeoAndTitle(Pawn pawn)
        {
            try
            {
                var parts = new List<string>();
                var role = pawn.Ideo?.GetRole(pawn);
                if (role != null)
                    parts.Add($"意识形态角色: {role.Label}");
                var title = pawn.royalty?.MostSeniorTitle;
                if (title != null)
                    parts.Add($"头衔: {title.def?.label}");
                return string.Join(" | ", parts);
            }
            catch (Exception) { return ""; }
        }

        private static string GetMentalState(Pawn pawn)
        {
            try
            {
                if (!pawn.InMentalState || pawn.MentalState == null) return "";
                return pawn.MentalState.InspectLine ?? pawn.MentalState.def.label;
            }
            catch (Exception) { return ""; }
        }

        private static string GetRecentLogs(Pawn pawn)
        {
            try
            {
                var entries = new List<(int Tick, LogEntry Entry)>();

                // 社交日志
                foreach (var e in Find.PlayLog.AllEntries)
                    if (e.Concerns(pawn))
                        entries.Add((e.Tick, e));

                // 战斗日志
                foreach (var b in Find.BattleLog.Battles)
                    if (b.Concerns(pawn))
                        foreach (var e in b.Entries)
                            if (e.Concerns(pawn))
                                entries.Add((e.Tick, e));

                if (entries.Count == 0) return "";

                var last2 = entries.OrderByDescending(x => x.Tick).Take(2)
                    .Select(x => x.Entry.ToGameStringFromPOV(pawn));
                return string.Join(" | ", last2);
            }
            catch (Exception) { return ""; }
        }

        private static bool IsFleeing(Pawn pawn)
        {
            if (pawn.InMentalState)
            {
                var def = pawn.MentalStateDef;
                if (def == MentalStateDefOf.PanicFlee || def == MentalStateDefOf.PanicFleeFire)
                    return true;
            }
            if (pawn.CurJob?.def == JobDefOf.Flee)
                return true;
            var lord = pawn.GetLord();
            if (lord?.CurLordToil is LordToil_PanicFlee)
                return true;
            return false;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
