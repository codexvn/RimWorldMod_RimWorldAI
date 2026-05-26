using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 获取指定殖民者的推荐衣物列表，按游戏内置评分从高到低排列。
    /// 复用 JobGiver_OptimizeApparel.ApparelScoreGain 评分逻辑。
    /// </summary>
    public class Tool_GetRecommendedApparel : ITool
    {
        public string Name => "get_recommended_apparel";
        public string Description => "获取指定殖民者的推荐衣物列表，按游戏内置更换衣物评分从高到低排列。输入殖民者 thingIDNumber，返回地图上所有可用衣物的评分排名。";

        private static readonly FieldInfo NeededWarmthField = typeof(JobGiver_OptimizeApparel)
            .GetField("neededWarmth", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo WornScoresField = typeof(JobGiver_OptimizeApparel)
            .GetField("wornApparelScores", BindingFlags.NonPublic | BindingFlags.Static);

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber（来自 get_colonists）" }
            },
            required = new[] { "colonist_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            if (!args.Value.TryGetProperty("colonist_id", out var jId) || !jId.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少 colonist_id 参数");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null) return ToolResult.Error($"未找到殖民者 ID={colonistId}");

                    if (pawn.outfits == null)
                        return ToolResult.Error($"{pawn.LabelShort} 没有着装方案。");

                    var policy = pawn.outfits.CurrentApparelPolicy;
                    if (policy == null) return ToolResult.Error($"{pawn.LabelShort} 没有当前着装方案。");

                    // 设置 neededWarmth（私有静态字段，供 ApparelScoreRaw 使用）
                    var neededWarmth = PawnApparelGenerator.CalculateNeededWarmth(
                        pawn, pawn.Map.TileInfo.tile, GenLocalDate.Twelfth(pawn));
                    NeededWarmthField?.SetValue(null, neededWarmth);

                    // 计算当前穿戴衣物的原始评分（供 ApparelScoreGain 做替换比较）
                    var wornApparel = pawn.apparel.WornApparel;
                    var wornScores = new List<float>();
                    if (WornScoresField != null)
                    {
                        if (WornScoresField.GetValue(null) is List<float> shared)
                        {
                            shared.Clear();
                            wornScores = shared;
                        }
                    }
                    foreach (var worn in wornApparel)
                        wornScores.Add(JobGiver_OptimizeApparel.ApparelScoreRaw(pawn, worn));

                    // 收集地图上所有可用衣物
                    var candidates = new List<(Apparel apparel, float score)>();
                    var tmpList = new List<Thing>();
                    map.listerThings.GetAllThings(tmpList, ThingRequestGroup.Apparel, null, true);

                    // 同时检查搬运目的地中的衣物
                    foreach (var haulSource in map.haulDestinationManager.AllHaulSourcesListForReading)
                    {
                        foreach (var thing in haulSource.GetDirectlyHeldThings())
                        {
                            if (thing is Apparel)
                                tmpList.Add(thing);
                        }
                    }

                    foreach (var t in tmpList)
                    {
                        if (t is not Apparel ap) continue;
                        if (ap.IsBurning()) continue;

                        // 着装方案过滤
                        if (!policy.filter.Allows(ap)) continue;

                        // 性别检查
                        if (ap.def.apparel.gender != Gender.None && ap.def.apparel.gender != pawn.gender)
                            continue;

                        // 发育阶段过滤
                        if (!ap.def.apparel.developmentalStageFilter.Has(pawn.DevelopmentalStage))
                            continue;

                        // 身体条件
                        if (!ApparelUtility.HasPartsToWear(pawn, ap.def)) continue;

                        // 生物编码检查
                        if (CompBiocodable.IsBiocoded(ap) && !CompBiocodable.IsBiocodedFor(ap, pawn))
                            continue;

                        float score = JobGiver_OptimizeApparel.ApparelScoreGain(pawn, ap, wornScores);
                        if (score >= -900f) // 排除极端否决项（-1000），保留低分但仍可选
                            candidates.Add((ap, score));
                    }

                    if (candidates.Count == 0)
                        return ToolResult.Success($"地图上没有 {pawn.LabelShort} 可用的推荐衣物。");

                    // 按分数降序排列
                    candidates.Sort((a, b) => b.score.CompareTo(a.score));

                    // 构建输出
                    var sb = new StringBuilder();
                    sb.AppendLine($"## {pawn.LabelShort} 推荐衣物（按评分降序）");
                    sb.AppendLine();
                    sb.AppendLine($"当前着装方案: **{policy.label}** | 保暖需求: **{neededWarmth}**");
                    sb.AppendLine();
                    sb.AppendLine("| 排名 | ID | 名称 | 评分 | 品质 | 利刃 | 钝击 | 隔热 | 层 | 位置 |");
                    sb.AppendLine("|------|-----|------|------|------|------|------|------|----|------|");

                    int rank = 0;
                    float prevScore = float.MaxValue;
                    foreach (var (ap, score) in candidates)
                    {
                        if (Math.Abs(score - prevScore) > 0.001f)
                            rank++;
                        prevScore = score;

                        var quality = ap.TryGetComp<CompQuality>()?.Quality.GetLabel() ?? "-";
                        float sharp = ap.GetStatValue(StatDefOf.ArmorRating_Sharp, true, -1);
                        float blunt = ap.GetStatValue(StatDefOf.ArmorRating_Blunt, true, -1);
                        float heat = ap.GetStatValue(StatDefOf.ArmorRating_Heat, true, -1);
                        string layer = ap.def.apparel?.LastLayer?.label ?? "-";
                        var pos = ap.Position;

                        // 标记已穿戴
                        string label = ap.Label;
                        if (wornApparel.Contains(ap))
                            label += " [已穿戴]";

                        sb.AppendLine($"| {rank} | {ap.thingIDNumber} | {label} | {score:F2} | {quality} | {sharp:P0} | {blunt:P0} | {heat:F0}°C | {layer} | ({pos.x},{pos.z}) |");
                    }

                    sb.AppendLine();
                    sb.Append($"共 {candidates.Count} 件可用衣物");
                    var best = candidates[0];
                    if (best.score > 0)
                    {
                        sb.Append($" | 推荐: {best.apparel.Label} (评分 {best.score:F2}, ID={best.apparel.thingIDNumber})");
                        sb.Append($" | 使用 equip_pawn(colonist_id={colonistId}, thing_id={best.apparel.thingIDNumber}) 装备");
                    }
                    else
                    {
                        sb.Append(" | 当前无可改善的衣物建议");
                    }

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"推荐衣物失败: {ex.Message}");
                }
            });
        }

        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
