using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_HoldCombatPosition : ITool, IRequiresAdvanceTick
    {
        public string Name => "hold_combat_position";
        public string Description => "批量命令殖民者前往战斗阵位并进入原版 Wait_Combat 待命。近战守位贴脸自动反击，远程开启 FireAtWill 自动射击可命中目标。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                positions = new
                {
                    type = "array",
                    description = "战斗阵位列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                            pos_x = new { type = "integer", description = "目标 X 坐标" },
                            pos_y = new { type = "integer", description = "目标 Y 坐标（映射到 IntVec3.z）" },
                            role = new
                            {
                                type = "string",
                                description = "站位角色: melee=近战守位, ranged=远程自动开火, hold=纯待命不主动远程开火",
                                @enum = new[] { "melee", "ranged", "hold" },
                                @default = "ranged"
                            }
                        },
                        required = new[] { "colonist_id", "pos_x", "pos_y" }
                    }
                }
            },
            required = new[] { "positions" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("positions", out var jPositions) || jPositions.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少必填参数: positions（需为数组）");

            var colonistCache = new Dictionary<int, Pawn>();
            var successList = new List<string>();
            var failList = new List<string>();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    foreach (JsonElement jPosition in jPositions.EnumerateArray())
                    {
                        try
                        {
                            if (!TryReadPosition(jPosition, out int colonistId, out int posX, out int posY, out string role, out string error))
                            {
                                failList.Add(error);
                                continue;
                            }

                            if (!colonistCache.TryGetValue(colonistId, out Pawn pawn))
                            {
                                pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                                if (pawn != null) colonistCache[colonistId] = pawn;
                            }

                            if (pawn == null)
                            {
                                failList.Add($"col={colonistId}: 找不到殖民者");
                                continue;
                            }

                            string? setupError = TrySetupHoldPosition(pawn, map, posX, posY, role);
                            if (setupError != null)
                            {
                                failList.Add($"{pawn.LabelShort}: {setupError}");
                                continue;
                            }

                            successList.Add($"{pawn.LabelShort}→({posX},{posY}) {GetRoleLabel(role)}");
                        }
                        catch (Exception ex)
                        {
                            failList.Add($"处理异常: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.Append($"已设置 {successList.Count}/{successList.Count + failList.Count} 个战斗阵位: {string.Join(", ", successList)}");
                    if (failList.Count > 0)
                        sb.Append($"。失败: {string.Join("; ", failList)}");
                    return successList.Count > 0 ? ToolResult.Success(sb.ToString()) : ToolResult.Error(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置战斗阵位失败: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }

        private static bool TryReadPosition(JsonElement jPosition, out int colonistId, out int posX, out int posY, out string role, out string error)
        {
            colonistId = 0;
            posX = 0;
            posY = 0;
            role = "ranged";
            error = "";

            if (!jPosition.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out colonistId))
            {
                error = "缺少 colonist_id";
                return false;
            }
            if (!jPosition.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out posX))
            {
                error = $"col={colonistId}: 缺少 pos_x";
                return false;
            }
            if (!jPosition.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out posY))
            {
                error = $"col={colonistId}: 缺少 pos_y";
                return false;
            }
            if (jPosition.TryGetProperty("role", out var jRole) && jRole.ValueKind == JsonValueKind.String)
                role = jRole.GetString() ?? "ranged";

            if (role != "melee" && role != "ranged" && role != "hold")
            {
                error = $"col={colonistId}: 未知 role={role}";
                return false;
            }

            return true;
        }

        private static string? TrySetupHoldPosition(Pawn pawn, Map map, int posX, int posY, string role)
        {
            if (pawn.Downed)
                return "已倒地，无法部署。";
            if (pawn.Deathresting)
                return "濒死休眠中，无法部署。";
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                return "无法移动。";
            if (pawn.drafter == null)
                return "无法征召。";
            if (role != "hold" && pawn.WorkTagIsDisabled(WorkTags.Violent))
                return "无法战斗。";
            if (role == "ranged" && pawn.equipment?.Primary?.def?.IsRangedWeapon != true)
                return "没有远程武器，不能设置为远程自动开火。";
            if (posX < 0 || posX >= map.Size.x || posY < 0 || posY >= map.Size.z)
                return $"坐标({posX},{posY})出界。";

            var dest = new IntVec3(posX, 0, posY);
            if (!dest.WalkableBy(map, pawn))
                return $"坐标({posX},{posY})不可站立。";
            if (!pawn.CanReach(dest, PathEndMode.OnCell, Danger.Deadly))
                return $"无法到达({posX},{posY})。";

            if (!pawn.Drafted)
                pawn.drafter.Drafted = true;
            pawn.drafter.FireAtWill = role == "ranged";

            Job waitJob = JobMaker.MakeJob(JobDefOf.Wait_Combat, dest);
            waitJob.expiryInterval = -1;
            waitJob.canUseRangedWeapon = role == "ranged";

            bool accepted;
            if (pawn.Position == dest)
            {
                accepted = pawn.jobs.TryTakeOrderedJob(waitJob, JobTag.Misc, false);
            }
            else
            {
                Job gotoJob = JobMaker.MakeJob(JobDefOf.Goto, dest);
                accepted = pawn.jobs.TryTakeOrderedJob(gotoJob, JobTag.Misc, false);
                if (accepted)
                    pawn.jobs.jobQueue.EnqueueLast(waitJob, JobTag.Misc);
            }

            return accepted ? null : "战斗阵位任务被阻塞。";
        }

        private static string GetRoleLabel(string role)
        {
            return role switch
            {
                "melee" => "近战守位",
                "ranged" => "远程自动开火",
                "hold" => "纯待命",
                _ => role
            };
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("positions", out var jPositions) || jPositions.ValueKind != JsonValueKind.Array)
                return null;

            int? minX = null, minZ = null, maxX = null, maxZ = null;
            foreach (JsonElement jPosition in jPositions.EnumerateArray())
            {
                if (jPosition.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out int x))
                {
                    minX = minX == null ? x : Math.Min(minX.Value, x);
                    maxX = maxX == null ? x : Math.Max(maxX.Value, x);
                }
                if (jPosition.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out int y))
                {
                    minZ = minZ == null ? y : Math.Min(minZ.Value, y);
                    maxZ = maxZ == null ? y : Math.Max(maxZ.Value, y);
                }
            }

            return minX.HasValue && minZ.HasValue && maxX.HasValue && maxZ.HasValue
                ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value)
                : ((int, int, int, int)?)null;
        }
    }
}
