using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_ConvertIdeo : ITool
    {
        public string Name => "convert_ideo";
        public string Description => "抽奖式意识形态转换。社交值（ConversionPower）和魅力值（PawnBeauty）越高成功概率越大。直接掷骰判定，成功则目标信仰立即变为执行者信仰。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doer_id = new { type = "integer", description = "执行转换的殖民者 ID（来自 get_colonists）" },
                target_id = new { type = "integer", description = "目标 ID（来自 get_colonists / find_pawn）" }
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

                    if (target == doer)
                        return ToolResult.Error("目标不能是自己。");

                    if (target.DevelopmentalStage.Baby())
                        return ToolResult.Error("婴儿无法转换信仰。");

                    if (Find.IdeoManager.classicMode)
                        return ToolResult.Error("经典模式无意识形态系统。");

                    var doerIdeo = doer.ideo?.Ideo;
                    var targetIdeo = target.ideo?.Ideo;
                    if (targetIdeo == null || doerIdeo == null)
                        return ToolResult.Error("目标或执行者没有意识形态。");

                    if (targetIdeo == doerIdeo)
                        return ToolResult.Error($"{target.Name.ToStringShort} 已经是 {targetIdeo.name} 信仰。");

                    float conversionPower = doer.GetStatValue(StatDefOf.ConversionPower);
                    float beauty = doer.GetStatValue(StatDefOf.PawnBeauty);

                    float baseChance = 0.2f + conversionPower * 0.25f;
                    float beautyBonus = Math.Max(beauty + 2f, 0f) * 0.05f;
                    float successChance = Math.Min(baseChance + beautyBonus, 0.9f);

                    var result = new StringBuilder();
                    result.AppendLine($"执行者: {doer.Name.ToStringShort}");
                    result.AppendLine($"目标: {target.Name.ToStringShort}（当前信仰: {targetIdeo.name}）");
                    result.AppendLine($"ConversionPower: {conversionPower:F2}");
                    result.AppendLine($"魅力值: {beauty:F2}");
                    result.AppendLine($"基础概率: {baseChance:P1}");
                    if (beautyBonus > 0f)
                        result.AppendLine($"魅力加成: {beautyBonus:P1}");
                    result.AppendLine($"总成功率: {successChance:P1}");

                    if (Rand.Chance(successChance))
                    {
                        target.ideo.SetIdeo(doerIdeo);
                        result.AppendLine();
                        result.AppendLine($"✓ 转换成功！{target.Name.ToStringShort} 现在信仰 {doerIdeo.name}。");
                        if (PawnUtility.ShouldSendNotificationAbout(doer) || PawnUtility.ShouldSendNotificationAbout(target))
                        {
                            string label = "LetterLabelConvertIdeoAttempt_Success".Translate();
                            string text = "LetterConvertIdeoAttempt_Success".Translate(
                                doer.Named("INITIATOR"), target.Named("RECIPIENT"),
                                doerIdeo.Named("IDEO"), targetIdeo.Named("OLDIDEO"));
                            Find.LetterStack.ReceiveLetter(label, text,
                                LetterDefOf.PositiveEvent, new LookTargets(doer, target));
                        }
                    }
                    else
                    {
                        result.AppendLine();
                        result.AppendLine($"✗ 转换失败。可尝试提升执行者的社交技能或魅力值后再试。");
                    }

                    return ToolResult.Success(result.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"转换失败: {ex.Message}");
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
