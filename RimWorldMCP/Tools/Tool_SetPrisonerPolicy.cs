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
    public class Tool_SetPrisonerPolicy : ITool, IRequiresAdvanceTick
    {
        private const int AutoCheckIntervalTicks = 250;
        private static readonly HashSet<int> s_convertThenRecruitTargets = new();
        private static int s_lastAutoCheckTick;

        public string Name => "set_prisoner_policy";
        public string Description => "设置囚犯政策。支持 recruit（劝导并招募）、convert_then_recruit（同化并招募）、reduce_resistance、convert、maintain_only、release、execute、enslave、reduce_will。默认会确保至少一名殖民者启用典狱工作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                target_id = new { type = "integer", description = "囚犯 thingIDNumber。单目标时使用。" },
                target_ids = new
                {
                    type = "array",
                    description = "囚犯 thingIDNumber 列表。批量设置时使用，优先级高于 target_id。",
                    items = new { type = "integer" }
                },
                policy = new
                {
                    type = "string",
                    description = "囚犯政策: recruit=劝导并招募, convert_then_recruit=同化并招募, reduce_resistance=只降低抵抗, convert=只同化文化/转换信仰, maintain_only=仅维持, release=释放, execute=处决, enslave=奴役, reduce_will=降低意志",
                    @enum = new[]
                    {
                        "recruit", "convert_then_recruit", "reduce_resistance", "convert",
                        "maintain_only", "release", "execute", "enslave", "reduce_will"
                    }
                },
                ensure_warden_work = new { type = "boolean", description = "若没有可用典狱员，自动启用一名殖民者的 Warden 工作。默认 true。", @default = true },
                warden_priority = new { type = "integer", description = "自动启用 Warden 时使用的优先级，1=最高，2=高。默认 1。", minimum = 1, maximum = 4, @default = 1 }
            },
            required = new[] { "policy" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            var targetIds = ParseTargetIds(args.Value);
            if (targetIds.Count == 0)
                return ToolResult.Error("缺少 target_id 或 target_ids。");

            if (!args.Value.TryGetProperty("policy", out var jPolicy) || jPolicy.ValueKind != JsonValueKind.String)
                return ToolResult.Error("缺少必填参数: policy");

            string policy = jPolicy.GetString() ?? "";
            bool ensureWardenWork = !args.Value.TryGetProperty("ensure_warden_work", out var jEnsure)
                || jEnsure.ValueKind != JsonValueKind.False;

            int wardenPriority = 1;
            if (args.Value.TryGetProperty("warden_priority", out var jPriority) && jPriority.TryGetInt32(out var parsedPriority))
                wardenPriority = Math.Max(1, Math.Min(4, parsedPriority));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var prisoners = map.mapPawns.PrisonersOfColonySpawned;
                    if (prisoners == null || prisoners.Count == 0)
                        return ToolResult.Error("当前地图没有殖民地囚犯。");

                    var results = new List<string>();
                    var failures = new List<string>();

                    foreach (int targetId in targetIds)
                    {
                        Pawn prisoner = prisoners.FirstOrDefault(p => p.thingIDNumber == targetId);
                        if (prisoner == null)
                        {
                            failures.Add($"ID={targetId}: 找不到殖民地囚犯");
                            continue;
                        }

                        string? failure = TryApplyPolicy(prisoner, policy);
                        if (failure != null)
                        {
                            failures.Add($"{prisoner.LabelShort}: {failure}");
                            continue;
                        }

                        results.Add($"{prisoner.LabelShort}: {GetPolicyLabel(policy)}");
                    }

                    string wardenMessage = ensureWardenWork
                        ? EnsureWardenWork(map, wardenPriority)
                        : "未检查典狱工作。";

                    var sb = new StringBuilder();
                    if (results.Count > 0)
                        sb.AppendLine($"已设置 {results.Count} 名囚犯政策: {string.Join("；", results)}");
                    if (failures.Count > 0)
                        sb.AppendLine($"失败: {string.Join("；", failures)}");
                    sb.AppendLine(wardenMessage);

                    bool hasSuccess = results.Count > 0;
                    return hasSuccess ? ToolResult.Success(sb.ToString().TrimEnd()) : ToolResult.Error(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置囚犯政策失败: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        public static void ProcessPendingAutoPolicies()
        {
            try
            {
                int currentTick = Find.TickManager?.TicksGame ?? 0;
                if (currentTick - s_lastAutoCheckTick < AutoCheckIntervalTicks)
                    return;

                s_lastAutoCheckTick = currentTick;
                if (s_convertThenRecruitTargets.Count == 0)
                    return;

                Map map = Find.CurrentMap;
                if (map == null)
                    return;

                var completed = new List<int>();
                foreach (int prisonerId in s_convertThenRecruitTargets.ToList())
                {
                    Pawn prisoner = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == prisonerId);
                    if (prisoner == null || prisoner.Dead || prisoner.Destroyed || prisoner.guest == null)
                    {
                        completed.Add(prisonerId);
                        continue;
                    }

                    if (!ModsConfig.IdeologyActive || Find.IdeoManager.classicMode)
                    {
                        completed.Add(prisonerId);
                        continue;
                    }

                    var targetIdeo = Faction.OfPlayer?.ideos?.PrimaryIdeo;
                    if (targetIdeo == null)
                    {
                        completed.Add(prisonerId);
                        continue;
                    }

                    if (prisoner.Ideo == targetIdeo)
                    {
                        prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.AttemptRecruit);
                        completed.Add(prisonerId);
                        McpLog.Info($"[PrisonerPolicy] {prisoner.LabelShort} 已完成同化，自动切换为劝导并招募。");
                    }
                    else if (prisoner.guest.ExclusiveInteractionMode != PrisonerInteractionModeDefOf.Convert)
                    {
                        completed.Add(prisonerId);
                    }
                }

                foreach (int id in completed)
                    s_convertThenRecruitTargets.Remove(id);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[PrisonerPolicy] 自动切换囚犯政策失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static List<int> ParseTargetIds(JsonElement args)
        {
            var ids = new List<int>();
            if (args.TryGetProperty("target_ids", out var jIds) && jIds.ValueKind == JsonValueKind.Array)
            {
                foreach (var jId in jIds.EnumerateArray())
                    if (jId.TryGetInt32(out int id)) ids.Add(id);
            }

            if (ids.Count == 0 && args.TryGetProperty("target_id", out var jTargetId) && jTargetId.TryGetInt32(out int targetId))
                ids.Add(targetId);

            return ids.Distinct().ToList();
        }

        private static string? TryApplyPolicy(Pawn prisoner, string policy)
        {
            if (prisoner.guest == null || !prisoner.IsPrisonerOfColony)
                return "目标不是殖民地囚犯。";
            if (prisoner.AnimalOrWildMan() && (policy == "recruit" || policy == "convert_then_recruit" || policy == "reduce_resistance" || policy == "convert"))
                return "动物或野人不适用该囚犯政策。";

            switch (policy)
            {
                case "maintain_only":
                    prisoner.guest.SetNoInteraction();
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                case "recruit":
                    if (!prisoner.guest.Recruitable)
                        return "该囚犯不可招募。";
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.AttemptRecruit);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                case "reduce_resistance":
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.ReduceResistance);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                case "release":
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Release);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                case "execute":
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Execution);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                case "convert":
                    return ApplyConvertPolicy(prisoner, recruitAfterConversion: false);
                case "convert_then_recruit":
                    if (!prisoner.guest.Recruitable)
                        return "该囚犯不可招募，无法设置为同化并招募。";
                    return ApplyConvertPolicy(prisoner, recruitAfterConversion: true);
                case "enslave":
                    if (!ModsConfig.IdeologyActive)
                        return "奴役需要 Ideology DLC。";
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Enslave);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                case "reduce_will":
                    if (!ModsConfig.IdeologyActive)
                        return "降低意志需要 Ideology DLC。";
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.ReduceWill);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                    return null;
                default:
                    return $"未知政策: {policy}";
            }
        }

        private static string? ApplyConvertPolicy(Pawn prisoner, bool recruitAfterConversion)
        {
            if (!ModsConfig.IdeologyActive || Find.IdeoManager.classicMode)
                return "当前没有可用的意识形态系统。";

            var targetIdeo = Faction.OfPlayer?.ideos?.PrimaryIdeo;
            if (targetIdeo == null)
                return "找不到玩家主要信仰。";

            if (prisoner.Ideo == targetIdeo)
            {
                if (recruitAfterConversion)
                {
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.AttemptRecruit);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                }
                else
                {
                    prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.MaintainOnly);
                    s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
                }
                return null;
            }

            prisoner.guest.ideoForConversion = targetIdeo;
            prisoner.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Convert);
            if (recruitAfterConversion)
                s_convertThenRecruitTargets.Add(prisoner.thingIDNumber);
            else
                s_convertThenRecruitTargets.Remove(prisoner.thingIDNumber);
            return null;
        }

        private static string EnsureWardenWork(Map map, int priority)
        {
            WorkTypeDef wardenDef = WorkTypeDefOf.Warden;
            var colonists = map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Downed && !p.Dead && !p.InMentalState && p.workSettings != null)
                .ToList();

            var activeWardens = colonists
                .Where(p => !p.WorkTypeIsDisabled(wardenDef) && p.workSettings.WorkIsActive(wardenDef))
                .OrderBy(p => p.workSettings.GetPriority(wardenDef))
                .ThenByDescending(GetNegotiationAbility)
                .ToList();

            if (activeWardens.Count > 0)
            {
                Pawn best = activeWardens[0];
                int currentPriority = best.workSettings.GetPriority(wardenDef);
                return $"典狱工作已启用: {best.LabelShort} (优先级 {currentPriority})。";
            }

            Pawn? candidate = colonists
                .Where(p => !p.WorkTypeIsDisabled(wardenDef) && p.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                .OrderByDescending(GetNegotiationAbility)
                .FirstOrDefault();

            if (candidate == null)
                return "警告：没有可用典狱员，囚犯政策不会被自动执行。请检查 Warden 工作、说话能力、倒地或精神状态。";

            if (!Current.Game.playSettings.useWorkPriorities)
                Current.Game.playSettings.useWorkPriorities = true;

            candidate.workSettings.SetPriority(wardenDef, priority);
            return $"已自动启用典狱工作: {candidate.LabelShort} → Warden 优先级 {priority}。";
        }

        private static float GetNegotiationAbility(Pawn pawn)
        {
            try
            {
                return pawn.GetStatValue(StatDefOf.NegotiationAbility, true, -1);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[PrisonerPolicy] 读取社交能力失败: {ex.GetType().Name}: {ex.Message}");
                return 0f;
            }
        }

        private static string GetPolicyLabel(string policy)
        {
            return policy switch
            {
                "recruit" => "劝导并招募",
                "convert_then_recruit" => "同化并招募",
                "reduce_resistance" => "只降低抵抗",
                "convert" => "只同化文化",
                "maintain_only" => "仅维持",
                "release" => "释放",
                "execute" => "处决",
                "enslave" => "奴役",
                "reduce_will" => "降低意志",
                _ => policy
            };
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            Map map = Find.CurrentMap;
            if (map == null) return null;

            var ids = ParseTargetIds(args.Value);
            int? minX = null, minZ = null, maxX = null, maxZ = null;
            foreach (int id in ids)
            {
                Pawn? pawn = CameraHelper.FindPawnById(map, id);
                if (pawn == null) continue;
                minX = minX == null ? pawn.Position.x : Math.Min(minX.Value, pawn.Position.x);
                minZ = minZ == null ? pawn.Position.z : Math.Min(minZ.Value, pawn.Position.z);
                maxX = maxX == null ? pawn.Position.x : Math.Max(maxX.Value, pawn.Position.x);
                maxZ = maxZ == null ? pawn.Position.z : Math.Max(maxZ.Value, pawn.Position.z);
            }

            return minX.HasValue && minZ.HasValue && maxX.HasValue && maxZ.HasValue
                ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value)
                : ((int, int, int, int)?)null;
        }
    }
}
