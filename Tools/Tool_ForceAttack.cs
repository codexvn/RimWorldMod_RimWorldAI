using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ForceAttack : ITool
    {
        public string Name => "force_attack";
        public string Description => "强制殖民者攻击指定目标，支持 ranged（原地射击）、melee（近战）和 chase（自动追击）三种模式。使用 thingIDNumber 指定目标。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                target_id = new { type = "integer", description = "目标 thingIDNumber（来自 find_enemies）" },
                attack_mode = new
                {
                    type = "string",
                    description = "攻击模式",
                    @enum = new[] { "ranged", "melee", "chase" },
                    @default = "chase"
                }
            },
            required = new[] { "colonist_id", "target_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id");

            string mode = "chase";
            if (args.Value.TryGetProperty("attack_mode", out var jMode))
            {
                mode = jMode.GetString() ?? "chase";
                if (mode != "ranged" && mode != "melee" && mode != "chase")
                    return ToolResult.Error($"未知攻击模式: {mode}。可选: ranged, melee, chase");
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    Pawn target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
                    if (target == null)
                        return ToolResult.Error($"找不到目标 ID={targetId}");

                    if (target.Dead || target.Destroyed)
                        return ToolResult.Error($"目标 {target.LabelShort} 已死亡或被销毁。");

                    // 自动征召
                    if (pawn.drafter != null && !pawn.Drafted)
                    {
                        pawn.drafter.Drafted = true;
                    }

                    if (!pawn.Drafted)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法被征召。");

                    // 可达性：ranged 模式只需视线可达，melee/chase 需要物理可达
                    bool canReach = mode == "ranged"
                        ? pawn.CanReach(target, PathEndMode.OnCell, Danger.Deadly)
                        : pawn.CanReach(target, PathEndMode.ClosestTouch, Danger.Deadly);

                    if (!canReach)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达目标 {target.LabelShort}。");

                    JobDef jobDef = mode == "ranged" ? JobDefOf.AttackStatic : JobDefOf.AttackMelee;
                    Job job = JobMaker.MakeJob(jobDef, target);

                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行攻击指令，目标可能已被预约。");

                    string modeLabel = mode == "ranged" ? "远程射击" : mode == "melee" ? "近战攻击" : "自动追击";
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已开始{modeLabel}目标 {target.LabelShort} (ID={targetId})。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制攻击失败: {ex.Message}");
                }
            });
        }
    }
}
