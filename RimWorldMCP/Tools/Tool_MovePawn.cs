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
    public class Tool_MovePawn : ITool
    {
        public string Name => "move_pawn";
        public string Description => "命令殖民者移动到指定坐标。通过游戏 Job 系统（Goto）让小人自然寻路走过去。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_name = new { type = "string", description = "殖民者名称" },
                pos_x = new { type = "integer", description = "目标 X 坐标（水平网格轴）" },
                pos_y = new { type = "integer", description = "目标 Y 坐标（垂直网格轴，映射到 IntVec3.z）" },
                queue = new { type = "boolean", description = "加入任务队列末尾而非立即执行（默认 true）", @default = true }
            },
            required = new[] { "colonist_name", "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_name", out var jName))
                return ToolResult.Error("缺少必填参数: colonist_name");

            string colonistName = jName.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(colonistName))
                return ToolResult.Error("colonist_name 不能为空");

            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            bool queue = true;
            if (args.Value.TryGetProperty("queue", out var jQueue) && jQueue.ValueKind == JsonValueKind.False)
                queue = false;
            var capQueue = queue;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c =>
                        c.Name.ToStringShort.IndexOf(colonistName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        c.Name.ToStringFull.IndexOf(colonistName, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者: {colonistName}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    if (posX < 0 || posX >= map.Size.x || posY < 0 || posY >= map.Size.z)
                        return ToolResult.Error($"目标坐标 ({posX},{posY}) 超出地图边界 (0~{map.Size.x - 1}, 0~{map.Size.z - 1})");

                    var dest = new IntVec3(posX, 0, posY);

                    if (!pawn.CanReach(dest, PathEndMode.OnCell, Danger.Deadly))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达 ({posX},{posY})");

                    if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法移动。");

                    Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, capQueue))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法执行移动指令，可能被阻塞。");
                    string queueLabel = capQueue ? "（已加入队列）" : "";

                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已开始移动到 ({posX}, {posY})。{queueLabel}");
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
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            return (posX, posY, posX, posY);
        }
    }
}
