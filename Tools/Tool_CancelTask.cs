using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_CancelTask : ITool
    {
        public string Name => "cancel_task";
        public string Description => "取消殖民者的当前任务或排队任务。类似游戏中的右键→取消当前工作/取消排队工作。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists），不传则取消所有殖民者" },
                cancel_current = new { type = "boolean", description = "是否取消当前正在执行的任务（默认 true）", @default = true },
                cancel_queued = new { type = "boolean", description = "是否取消排队中的任务（默认 true）", @default = true }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    bool cancelCurrent = true;
                    bool cancelQueued = true;

                    if (args != null)
                    {
                        if (args.Value.TryGetProperty("cancel_current", out var jCc) && jCc.ValueKind == JsonValueKind.False)
                            cancelCurrent = false;
                        if (args.Value.TryGetProperty("cancel_queued", out var jCq) && jCq.ValueKind == JsonValueKind.False)
                            cancelQueued = false;
                    }

                    if (!cancelCurrent && !cancelQueued)
                        return ToolResult.Error("cancel_current 和 cancel_queued 不能同时为 false。");

                    // 确定目标殖民者
                    int? targetId = null;
                    if (args != null && args.Value.TryGetProperty("colonist_id", out var jCid) && jCid.TryGetInt32(out var cid))
                        targetId = cid;

                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned
                        .Where(c => targetId == null || c.thingIDNumber == targetId)
                        .ToList();

                    if (colonists.Count == 0)
                        return ToolResult.Error(targetId.HasValue
                            ? $"未找到殖民者 ID={targetId}"
                            : "没有可用的殖民者。");

                    int stoppedCount = 0;
                    int clearedCount = 0;
                    var affectedNames = new System.Collections.Generic.List<string>();

                    foreach (var pawn in colonists)
                    {
                        bool hadJob = pawn.jobs.curJob != null;
                        bool hadQueue = pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Count > 0;

                        if (cancelCurrent && cancelQueued)
                        {
                            pawn.jobs.StopAll(false, true);
                            if (hadJob || hadQueue) affectedNames.Add(pawn.Name.ToStringShort);
                            if (hadJob) stoppedCount++;
                            if (hadQueue) clearedCount++;
                        }
                        else if (cancelCurrent)
                        {
                            if (hadJob)
                            {
                                pawn.jobs.StopAll(false, true);
                                stoppedCount++;
                                affectedNames.Add(pawn.Name.ToStringShort);
                            }
                        }
                        else if (cancelQueued)
                        {
                            if (hadQueue)
                            {
                                pawn.jobs.ClearQueuedJobs(true);
                                clearedCount++;
                                affectedNames.Add(pawn.Name.ToStringShort);
                            }
                        }
                    }

                    if (affectedNames.Count == 0)
                        return ToolResult.Success("没有需要取消的任务。");

                    string action = cancelCurrent && cancelQueued ? "取消当前及排队任务"
                        : cancelCurrent ? "取消当前任务" : "清空排队任务";

                    var sb = new System.Text.StringBuilder();
                    sb.Append($"已{action}: ");
                    sb.Append(string.Join(", ", affectedNames));
                    if (stoppedCount > 0) sb.Append($" | 中断当前: {stoppedCount}人");
                    if (clearedCount > 0) sb.Append($" | 清空排队: {clearedCount}人");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"取消任务失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("colonist_id", out var jC) || !jC.TryGetInt32(out var id)) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            var pawn = CameraHelper.FindPawnById(map, id);
            if (pawn == null) return null;
            return (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
        }
    }
}
