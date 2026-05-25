using System;
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
    public class Tool_FindEnemies : ITool
    {
        public string Name => "find_enemies";
        public string Description => "查找地图上所有敌对目标（含逃跑中状态），并列出每个已征召殖民者可攻击的敌人编号列表。攻击范围覆盖 = 武器射程 + 射速/近战。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var playerFaction = Faction.OfPlayer;
                    if (playerFaction == null) return ToolResult.Error("无法获取玩家派系。");

                    var enemies = map.mapPawns.AllPawnsSpawned
                        .Where(p => p.HostileTo(playerFaction) && !p.Dead && !p.Destroyed)
                        .OrderBy(p => p.Position.x)
                        .ThenBy(p => p.Position.z)
                        .ToList();

                    if (enemies.Count == 0)
                        return ToolResult.Success("地图上没有发现敌对目标。");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 敌对目标 ({enemies.Count})");
                    sb.AppendLine("| thingIDNumber | 名称 | 类型 | 坐标 | 状态 |");
                    sb.AppendLine("|---|---:|---:|---|");
                    foreach (var e in enemies)
                    {
                        string status = e.Downed ? "倒地"
                            : IsFleeing(e) ? "逃跑中"
                            : e.Drafted ? "征召"
                            : "活跃";
                        sb.AppendLine($"| {e.thingIDNumber} | {e.LabelShort} | {e.KindLabel} | ({e.Position.x},{e.Position.z}) | {status} |");
                    }

                    // 已征召殖民者的攻击范围覆盖
                    var drafted = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .Where(p => p.Drafted && !p.Downed)
                        .ToList();

                    if (drafted.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("## 已征召殖民者 → 可攻击目标");
                        sb.AppendLine();
                        sb.AppendLine("| 殖民者 (ID) | 武器 | 类型 | 射程 | 可攻击 (ID) |");
                        sb.AppendLine("|---|---:|---:|---|");

                        foreach (var d in drafted)
                        {
                            var verb = d.CurrentEffectiveVerb;
                            bool isMelee = verb?.verbProps.IsMeleeAttack ?? true;
                            float effectiveRange = verb?.EffectiveRange ?? 1.42f;

                            string weaponLabel;
                            if (d.equipment?.Primary != null)
                                weaponLabel = d.equipment.Primary.Label;
                            else if (isMelee)
                                weaponLabel = "空手/近战";
                            else
                                weaponLabel = "无武器";

                            string atkType = isMelee ? "近战" : "远程";

                            // 找出可攻击的敌人
                            var inRange = enemies
                                .Where(e =>
                                {
                                    float distSq = d.Position.DistanceToSquared(e.Position);
                                    float rangeSq = effectiveRange * effectiveRange;
                                    return distSq <= rangeSq;
                                })
                                .Select(e => e.thingIDNumber.ToString())
                                .ToList();

                            string rangeDisplay = isMelee ? "邻接" : effectiveRange.ToString("F0");
                            string targetsDisplay = inRange.Count > 0
                                ? string.Join(", ", inRange)
                                : "-";

                            sb.AppendLine($"| {d.LabelShort} ({d.thingIDNumber}) | {weaponLabel} | {atkType} | {rangeDisplay} | {targetsDisplay} |");
                        }
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"查找敌人失败: {ex.Message}");
                }
            });
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
    }
}
