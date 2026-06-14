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
    public class Tool_MovePawn : ITool, IRequiresAdvanceTick
    {
        public string Name => "move_pawn";
        public string Description => "批量命令殖民者移动到指定坐标。通过游戏 Job 系统（Goto）让小人自然寻路走过去。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                moves = new
                {
                    type = "array",
                    description = "移动指令列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                            pos_x = new { type = "integer", description = "目标 X 坐标" },
                            pos_y = new { type = "integer", description = "目标 Y 坐标（映射到 IntVec3.z）" }
                        },
                        required = new[] { "colonist_id", "pos_x", "pos_y" }
                    }
                }
            },
            required = new[] { "moves" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("moves", out var jMoves) || jMoves.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少必填参数: moves（需为数组）");

            var colonistCache = new Dictionary<int, Pawn>();
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

                    foreach (JsonElement jMove in jMoves.EnumerateArray())
                    {
                        try
                        {
                            if (!jMove.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                            { failList.Add($"缺少 colonist_id"); continue; }
                            if (!jMove.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                            { failList.Add($"col={colonistId}: 缺少 pos_x"); continue; }
                            if (!jMove.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                            { failList.Add($"col={colonistId}: 缺少 pos_y"); continue; }

                            if (!colonistCache.TryGetValue(colonistId, out var pawn))
                            {
                                pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                                if (pawn != null) colonistCache[colonistId] = pawn;
                            }
                            if (pawn == null) { failList.Add($"col={colonistId}: 找不到"); continue; }

                            if (posX < 0 || posX >= map.Size.x || posY < 0 || posY >= map.Size.z)
                            { failList.Add($"{pawn.LabelShort}: 坐标({posX},{posY})出界"); continue; }

                            var dest = new IntVec3(posX, 0, posY);
                            if (!pawn.CanReach(dest, PathEndMode.OnCell, Danger.Deadly))
                            { failList.Add($"{pawn.LabelShort}: 无法到达({posX},{posY})"); continue; }

                            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                            { failList.Add($"{pawn.LabelShort}: 无法移动"); continue; }

                            if (!pawn.Drafted && pawn.drafter != null)
                                pawn.drafter.Drafted = true;

                            Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
                            // Replace: 战斗移动，立即打断
                            if (!JobQueueHelper.TryTake(pawn, job, QueueMode.Replace))
                            { failList.Add($"{pawn.LabelShort}: 移动被阻塞"); continue; }

                            successList.Add($"{pawn.LabelShort}→({posX},{posY})");
                        }
                        catch (Exception ex)
                        { failList.Add($"异常: {ex.Message}"); }
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.Append($"已发送 {successList.Count}/{successList.Count + failList.Count} 个移动指令: {string.Join(", ", successList)}");
                    if (failList.Count > 0)
                        sb.Append($"。失败: {string.Join("; ", failList)}");
                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"移动命令失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("moves", out var jMoves) || jMoves.ValueKind != JsonValueKind.Array) return null;
            int? minX = null, minZ = null, maxX = null, maxZ = null;
            foreach (var jm in jMoves.EnumerateArray())
            {
                if (jm.TryGetProperty("pos_x", out var jX) && jX.TryGetInt32(out var px))
                { minX = minX == null ? px : Math.Min(minX.Value, px); maxX = maxX == null ? px : Math.Max(maxX.Value, px); }
                if (jm.TryGetProperty("pos_y", out var jY) && jY.TryGetInt32(out var py))
                { minZ = minZ == null ? py : Math.Min(minZ.Value, py); maxZ = maxZ == null ? py : Math.Max(maxZ.Value, py); }
            }
            return (minX != null) ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value) : ((int, int, int, int)?)null;
        }
    }
}
