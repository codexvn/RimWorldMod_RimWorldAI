using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    /// <summary>
    /// 射击位评分排名工具 — 复刻游戏 CastPositionFinder.CastPositionPreference() 公式。
    /// 源码: Assembly-CSharp/Verse/AI/CastPositionFinder.cs:259-314
    /// </summary>
    public class Tool_ShootingPositionGrid : ITool
    {
        public string Name => "shooting_position_grid";
        public string Description => "在矩形范围内计算前 N 个最佳射击位置排名（复刻游戏原版 CastPositionPreference 评分公式）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                target_id = new { type = "integer", description = "目标 thingIDNumber（来自 find_enemies）" },
                pos_x = new { type = "integer", description = "搜索范围左下角 X" },
                pos_y = new { type = "integer", description = "搜索范围左下角 Y" },
                end_x = new { type = "integer", description = "搜索范围右上角 X（不提供则只查 pos_x,pos_y 单格）" },
                end_y = new { type = "integer", description = "搜索范围右上角 Y" },
                top_n = new { type = "integer", description = "返回前几名（默认 5）", @default = 5 }
            },
            required = new[] { "colonist_id", "target_id", "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;

            int topN = 5;
            if (args.Value.TryGetProperty("top_n", out var jTN) && jTN.TryGetInt32(out var tn) && tn > 0) topN = tn;

            return await McpCommandQueue.DispatchAsync(() => Execute(colonistId, targetId, posX, posY, endX, endY, topN));
        }

        private ToolResult Execute(int colonistId, int targetId, int minX, int minZ, int maxX, int maxZ, int topN)
        {
            try
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("没有当前地图。");

                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                Pawn caster = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                if (caster == null) return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                Pawn target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
                if (target == null) return ToolResult.Error($"找不到目标 ID={targetId}");
                if (target.Dead || target.Destroyed) return ToolResult.Error("目标已死亡或被销毁。");

                // 获取远程攻击 verb
                if (caster.drafter != null && !caster.Drafted)
                    caster.drafter.Drafted = true;

                Verb verb = caster.CurrentEffectiveVerb;
                if (verb == null || verb.verbProps.IsMeleeAttack)
                {
                    // 尝试从装备武器获取
                    var eqVerb = caster.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb;
                    if (eqVerb != null && !eqVerb.verbProps.IsMeleeAttack)
                        verb = eqVerb;
                    else
                        return ToolResult.Error($"{caster.LabelShort} 没有远程武器。");
                }

                float effectiveRange = verb.EffectiveRange;
                IntVec3 casterPos = caster.Position;
                IntVec3 targetPos = target.Position;

                // 裁剪搜索范围到地图边界
                minX = Math.Max(0, minX); minZ = Math.Max(0, minZ);
                maxX = Math.Min(map.Size.x - 1, maxX); maxZ = Math.Min(map.Size.z - 1, maxZ);

                // 原版常数
                const float basePreference = 0.3f;
                const float coverPreferenceFactor = 0.55f;
                const float distanceDecayBase = 0.967f;

                float rangeFromTarget = (casterPos - targetPos).LengthHorizontal;
                float rangeFromTargetSquared = rangeFromTarget * rangeFromTarget;
                // effectiveRange 已是武器射程，0.8 = 最佳位置在 80% 射程处（与游戏 CastPositionFinder 一致）
                float optimalRange = effectiveRange * 0.8f;
                float optimalRangeSquared = optimalRange * optimalRange;

                bool wantCover = effectiveRange > 5f;

                var scoredPositions = new List<(IntVec3 pos, float score, float coverPct, float distToTarget)>();

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        var c = new IntVec3(x, 0, z);

                        // 滤网1: 可站立
                        if (!c.WalkableBy(map, caster)) continue;

                        // 滤网2: 可达
                        if (!map.reachability.CanReach(casterPos, c, PathEndMode.OnCell, TraverseParms.For(caster, Danger.Deadly, TraverseMode.ByPawn))) continue;

                        // 滤网3: 未被预定
                        if (!map.pawnDestinationReservationManager.CanReserve(c, caster, false)) continue;

                        // 滤网4: 无已知危险
                        if (PawnUtility.KnownDangerAt(c, map, caster)) continue;

                        // 滤网5: 能打到目标
                        if (!verb.CanHitTargetFrom(c, target)) continue;

                        // 滤网6: 无火
                        bool hasFire = false;
                        bool passThroughOnly = false;
                        var thingsAt = map.thingGrid.ThingsListAtFast(c);
                        for (int ti = 0; ti < thingsAt.Count; ti++)
                        {
                            if (thingsAt[ti] is Fire f && f.parent == null) { hasFire = true; break; }
                            if (thingsAt[ti].def.passability == Traversability.PassThroughOnly) passThroughOnly = true;
                        }
                        if (hasFire) continue;

                        // ===== 评分: CastPositionPreference 复刻 =====
                        float score = basePreference;

                        // Step2: 掩体加分
                        float coverPct = 0f;
                        if (wantCover)
                        {
                            coverPct = CoverUtility.CalculateOverallBlockChance(c, targetPos, map);
                            score += coverPct * coverPreferenceFactor;
                        }

                        // Step3: 移动距离衰减
                        float distFromCaster = (casterPos - c).LengthHorizontal;
                        float adjustedDist = distFromCaster;
                        if (rangeFromTarget > 100f)
                        {
                            adjustedDist -= (rangeFromTarget - 100f);
                            if (adjustedDist < 0f) adjustedDist = 0f;
                        }
                        score *= (float)Math.Pow(distanceDecayBase, adjustedDist);

                        // Step4: 最优距离吻合度
                        float rangeToTargetSq = (c - targetPos).LengthHorizontalSquared;
                        float ratio = 1f - Math.Abs(rangeToTargetSq - optimalRangeSquared) / optimalRangeSquared;
                        ratio = 0.7f + 0.3f * ratio;
                        score *= ratio;

                        // Step5: 太近惩罚
                        if (rangeToTargetSq < 25f) score *= 0.5f;

                        // Step6: 比当前位置离目标更远惩罚
                        float fromCasterToCellSq = (c - casterPos).LengthHorizontalSquared;
                        if (fromCasterToCellSq > rangeFromTargetSquared) score *= 0.4f;

                        // Step7: 通道格惩罚
                        if (passThroughOnly) score *= 0.4f;

                        scoredPositions.Add((c, score, coverPct, (float)Math.Sqrt(rangeToTargetSq)));
                    }
                }

                if (scoredPositions.Count == 0)
                    return ToolResult.Error("搜索范围内没有可用的射击位置（无法到达、打不到目标或无视线）。");

                // 排序取 top N
                var top = scoredPositions.OrderByDescending(s => s.score).Take(topN).ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"### 最佳射击位 (Top {top.Count}) — {caster.LabelShort} → {target.LabelShort}");
                sb.AppendLine($"武器: {verb.EquipmentSource?.Label ?? "武器"} | 有效射程: {effectiveRange:F0} | 最优距离: {(float)Math.Sqrt(optimalRangeSquared):F0}格");
                sb.AppendLine();
                sb.AppendLine("| # | 坐标 | 距目标 | 掩体% | 评分 | 说明 |");
                sb.AppendLine("|---|------|--------|-------|------|------|");

                string Explain(int i, float s, float c, float d)
                {
                    var parts = new List<string>();
                    float rangePct = d / effectiveRange * 100f;
                    if (c >= 0.5f) parts.Add("掩体好");
                    else if (c >= 0.2f) parts.Add("有掩体");
                    if (Math.Abs(rangePct - 80f) < 10f) parts.Add("距离适中");
                    else if (rangePct < 30f) parts.Add("偏近");
                    else if (rangePct > 85f) parts.Add("偏远");
                    if (parts.Count == 0) parts.Add("-");
                    return string.Join("+", parts);
                }

                for (int i = 0; i < top.Count; i++)
                {
                    var (pos, score, coverPct, dist) = top[i];
                    float rangePct = dist / effectiveRange * 100f;
                    sb.AppendLine($"| {i + 1} | ({pos.x},{pos.z}) | {dist:F0}格({rangePct:F0}%) | {(coverPct * 100f):F0}% | {score:F2} | {Explain(i, score, coverPct, dist)} |");
                }

                return ToolResult.Success(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"shooting_position_grid 失败: {ex.Message}");
            }
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var px)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var py)) return null;
            int ex = px, ey = py;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var v)) ex = v;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var v2)) ey = v2;
            return (Math.Min(px, ex), Math.Min(py, ey), Math.Max(px, ex), Math.Max(py, ey));
        }
    }
}
