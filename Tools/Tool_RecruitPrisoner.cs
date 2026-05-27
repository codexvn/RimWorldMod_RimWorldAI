using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_RecruitPrisoner : ITool
    {
        public string Name => "recruit_prisoner";
        public string Description => "抽奖式招募囚犯。社交值（NegotiationAbility）和魅力值（PawnBeauty）越高成功概率越大。成功则囚犯直接加入殖民地。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行招募的殖民者 ID（来自 get_colonists）" },
                target_id = new { type = "integer", description = "囚犯 ID（来自 get_colonists / find_pawn）" }
            },
            required = new[] { "doer_id", "target_id" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return Task.FromResult(ToolResult.Error("缺少参数"));
            if (!args.Value.TryGetProperty("doer_id", out var jDid) || !jDid.TryGetInt32(out var doerId))
                return Task.FromResult(ToolResult.Error("缺少必填参数: doer_id"));
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return Task.FromResult(ToolResult.Error("缺少必填参数: target_id"));

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    var doer = colonists.FirstOrDefault(c => c.thingIDNumber == doerId);
                    if (doer == null)
                        return ToolResult.Error($"找不到执行者 ID={doerId}");

                    Map map = doer.Map;
                    if (map == null)
                        return ToolResult.Error("执行者不在当前地图。");

                    var target = map.mapPawns.AllPawnsSpawned
                        .FirstOrDefault(p => p.thingIDNumber == targetId);
                    if (target == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    if (!target.IsPrisonerOfColony)
                        return ToolResult.Error($"{target.Name.ToStringShort} 不是殖民地囚犯。");

                    if (target.AnimalOrWildMan())
                        return ToolResult.Error("动物或野人无法通过此方式招募。");

                    float negotiationAbility = doer.GetStatValue(StatDefOf.NegotiationAbility);
                    float beauty = doer.GetStatValue(StatDefOf.PawnBeauty);

                    float baseChance = 0.2f + negotiationAbility * 0.25f;
                    float beautyBonus = Math.Max(beauty + 2f, 0f) * 0.05f;
                    float successChance = Math.Min(baseChance + beautyBonus, 0.9f);

                    var result = new StringBuilder();
                    result.AppendLine($"执行者: {doer.Name.ToStringShort}");
                    result.AppendLine($"目标: {target.Name.ToStringShort}（抵抗: {target.guest.Resistance:F1}）");
                    result.AppendLine($"NegotiationAbility: {negotiationAbility:F2}");
                    result.AppendLine($"魅力值: {beauty:F2}");
                    result.AppendLine($"基础概率: {baseChance:P1}");
                    if (beautyBonus > 0f)
                        result.AppendLine($"魅力加成: {beautyBonus:P1}");
                    result.AppendLine($"总成功率: {successChance:P1}");

                    if (Rand.Chance(successChance))
                    {
                        target.guest.resistance = 0f;
                        string label, text;
                        InteractionWorker_RecruitAttempt.DoRecruit(doer, target, out label, out text, true, false);
                        result.AppendLine();
                        result.AppendLine($"✓ 招募成功！{target.Name.ToStringShort} 已加入殖民地。");
                        if (!label.NullOrEmpty())
                        {
                            Find.LetterStack.ReceiveLetter(label, text,
                                LetterDefOf.PositiveEvent, new LookTargets(doer, target));
                        }
                    }
                    else
                    {
                        float resistanceDrop = 0.1f * negotiationAbility;
                        float oldResistance = target.guest.Resistance;
                        target.guest.resistance = Math.Max(0f, target.guest.resistance - resistanceDrop);
                        float actualDrop = oldResistance - target.guest.Resistance;
                        result.AppendLine();
                        result.AppendLine($"✗ 招募失败。抵抗降低 {actualDrop:F1}（当前: {target.guest.Resistance:F1}）。");
                        if (target.guest.Resistance <= 0f)
                        {
                            result.AppendLine("抵抗已归零！下次可尝试 direct_recruit 或等待典狱官自然招募。");
                        }
                    }

                    return ToolResult.Success(result.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"招募失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("doer_id", out var jA) || !jA.TryGetInt32(out var idA)) return null;
            if (!args.Value.TryGetProperty("target_id", out var jB) || !jB.TryGetInt32(out var idB)) return null;
            var a = CameraHelper.FindPawnById(map, idA);
            var b = CameraHelper.FindPawnById(map, idB);
            if (a == null || b == null) return null;
            return (Math.Min(a.Position.x, b.Position.x), Math.Min(a.Position.z, b.Position.z),
                    Math.Max(a.Position.x, b.Position.x), Math.Max(a.Position.z, b.Position.z));
        }
    }
}
