using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ForceAttack : ITool, IRequiresAdvanceTick
    {
        public string Name => "force_attack";
        public string Description => "批量命令殖民者攻击指定目标。attack_mode 支持 melee（近战追击）、hold_position（原地射击，敌出射程自动停）、auto（根据武器自动选择）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                attacks = new
                {
                    type = "array",
                    description = "攻击指令列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                            target_id = new { type = "integer", description = "目标 thingIDNumber（来自 find_enemies）" },
                            attack_mode = new
                            {
                                type = "string",
                                description = "攻击模式: melee=近战追击, hold_position=原地射击不移动, auto=根据武器自动选",
                                @enum = new[] { "melee", "hold_position", "auto" },
                                @default = "auto"
                            }
                        },
                        required = new[] { "colonist_id", "target_id" }
                    }
                }
            },
            required = new[] { "attacks" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("attacks", out var jAttacks) || jAttacks.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少必填参数: attacks（需为数组）");

            var colonistCache = new Dictionary<int, Pawn>();
            var targetCache = new Dictionary<int, Pawn>();
            var successList = new List<string>();
            var failList = new List<string>();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    foreach (JsonElement jAttack in jAttacks.EnumerateArray())
                    {
                        try
                        {
                            if (!jAttack.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                            {
                                failList.Add($"缺少 colonist_id");
                                continue;
                            }
                            if (!jAttack.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                            {
                                failList.Add($"col={colonistId}: 缺少 target_id");
                                continue;
                            }

                            string mode = "auto";
                            if (jAttack.TryGetProperty("attack_mode", out var jMode) && jMode.ValueKind == JsonValueKind.String)
                            {
                                mode = jMode.GetString() ?? "auto";
                                if (mode != "melee" && mode != "hold_position" && mode != "auto" && mode != "ranged" && mode != "chase")
                                {
                                    failList.Add($"col={colonistId}: 未知模式 {mode}");
                                    continue;
                                }
                                // 兼容旧名
                                if (mode == "ranged") mode = "hold_position";
                                if (mode == "chase") mode = "auto";
                            }

                            // 缓存查找
                            if (!colonistCache.TryGetValue(colonistId, out var pawn))
                            {
                                pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                                if (pawn != null) colonistCache[colonistId] = pawn;
                            }
                            if (pawn == null) { failList.Add($"col={colonistId}: 找不到"); continue; }

                            if (!targetCache.TryGetValue(targetId, out var target))
                            {
                                target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
                                if (target != null) targetCache[targetId] = target;
                            }
                            if (target == null) { failList.Add($"{pawn.LabelShort}: 找不到目标 ID={targetId}"); continue; }
                            if (target.Dead || target.Destroyed) { failList.Add($"{pawn.LabelShort}: 目标 {target.LabelShort} 已死"); continue; }

                            // 自动征召
                            if (pawn.drafter != null && !pawn.Drafted)
                                pawn.drafter.Drafted = true;
                            if (!pawn.Drafted) { failList.Add($"{pawn.LabelShort}: 无法征召"); continue; }

                            // 选择攻击 Job
                            JobDef jobDef;
                            bool holdPosition = mode == "hold_position";
                            if (mode == "hold_position" || mode == "auto")
                            {
                                if (pawn.equipment?.Primary?.def?.IsRangedWeapon == true)
                                    jobDef = JobDefOf.AttackStatic;
                                else if (mode == "hold_position")
                                { failList.Add($"{pawn.LabelShort}: 无远程武器，无法hold_position"); continue; }
                                else
                                    jobDef = JobDefOf.AttackMelee;
                            }
                            else // melee
                                jobDef = JobDefOf.AttackMelee;

                            // 可达性
                            PathEndMode peMode = jobDef == JobDefOf.AttackMelee ? PathEndMode.ClosestTouch : PathEndMode.OnCell;
                            if (!pawn.CanReach(target, peMode, Danger.Deadly))
                            { failList.Add($"{pawn.LabelShort}: 无法到达 {target.LabelShort}"); continue; }

                            Job job = JobMaker.MakeJob(jobDef, target);
                            job.expiryInterval = -1;
                            if (jobDef == JobDefOf.AttackStatic)
                            {
                                job.maxNumStaticAttacks = int.MaxValue;
                                if (holdPosition)
                                    job.endIfCantShootTargetFromCurPos = true;
                            }
                            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                            { failList.Add($"{pawn.LabelShort}: 无法执行攻击"); continue; }

                            string modeLabel = mode == "hold_position" ? "hold_position" : mode == "melee" ? "近战" : "auto";
                            successList.Add($"{pawn.LabelShort}{getArrow(jobDef)}→{target.LabelShort}({modeLabel})");
                        }
                        catch (Exception ex)
                        {
                            failList.Add($"处理异常: {ex.Message}");
                        }
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.Append($"已发送 {successList.Count}/{successList.Count + failList.Count} 个攻击指令: {string.Join(", ", successList)}");
                    if (failList.Count > 0)
                        sb.Append($"。失败: {string.Join("; ", failList)}");
                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制攻击失败: {ex.Message}");
                }
            });
        }

        private static string getArrow(JobDef def)
        {
            return def == JobDefOf.AttackStatic ? "⇢" : "→";
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("attacks", out var jAttacks) || jAttacks.ValueKind != JsonValueKind.Array) return null;
            int? minX = null, minZ = null, maxX = null, maxZ = null;
            foreach (var ja in jAttacks.EnumerateArray())
            {
                if (ja.TryGetProperty("colonist_id", out var jC) && jC.TryGetInt32(out var cid))
                {
                    var p = CameraHelper.FindPawnById(map, cid);
                    if (p != null)
                    {
                        minX = minX == null ? p.Position.x : Math.Min(minX.Value, p.Position.x);
                        minZ = minZ == null ? p.Position.z : Math.Min(minZ.Value, p.Position.z);
                        maxX = maxX == null ? p.Position.x : Math.Max(maxX.Value, p.Position.x);
                        maxZ = maxZ == null ? p.Position.z : Math.Max(maxZ.Value, p.Position.z);
                    }
                }
                if (ja.TryGetProperty("target_id", out var jT) && jT.TryGetInt32(out var tid))
                {
                    var t = CameraHelper.FindPawnById(map, tid);
                    if (t != null)
                    {
                        minX = minX == null ? t.Position.x : Math.Min(minX.Value, t.Position.x);
                        minZ = minZ == null ? t.Position.z : Math.Min(minZ.Value, t.Position.z);
                        maxX = maxX == null ? t.Position.x : Math.Max(maxX.Value, t.Position.x);
                        maxZ = maxZ == null ? t.Position.z : Math.Max(maxZ.Value, t.Position.z);
                    }
                }
            }
            return (minX != null) ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value) : ((int, int, int, int)?)null;
        }
    }
}
