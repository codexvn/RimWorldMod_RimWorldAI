using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_SetWorkPriority : ITool
    {
        public string Name => "set_work_priority";
        public string Description => "批量设置殖民者的工作优先级 (0-4)。0=不分配，1=最高优先，4=最低优先。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                priorities = new
                {
                    type = "array",
                    description = "工作优先级列表",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            work_type = new
                            {
                                type = "string",
                                description = "工作类型 defName",
                                @enum = new[]
                                {
                                    "Firefighter", "Doctor", "Patient", "PatientBedRest", "BasicWorker",
                                    "Warden", "Handling", "Cooking", "Construction", "Repair",
                                    "Growing", "Mining", "PlantCutting", "Smithing", "Tailoring",
                                    "Art", "Crafting", "Hauling", "Cleaning", "Research"
                                }
                            },
                            priority = new { type = "integer", description = "优先级: 0=不分配, 1=最高, 2=高, 3=普通, 4=最低", minimum = 0, maximum = 4 }
                        },
                        required = new[] { "work_type", "priority" }
                    }
                }
            },
            required = new[] { "colonist_id", "priorities" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");
            if (!args.Value.TryGetProperty("priorities", out var jp) || jp.ValueKind != JsonValueKind.Array)
                return ToolResult.Error("缺少 priorities（需为数组）");

            var entries = new List<(string workType, int priority)>();
            foreach (var item in jp.EnumerateArray())
            {
                if (!item.TryGetProperty("work_type", out var wt) || !item.TryGetProperty("priority", out var p) || !p.TryGetInt32(out var pri))
                    return ToolResult.Error("priorities 中每项必须包含 work_type(string) 和 priority(int)");
                if (pri < 0 || pri > 4)
                    return ToolResult.Error($"priority 必须在 0-4 之间: {pri}");
                entries.Add((wt.GetString() ?? "", pri));
            }

            if (entries.Count == 0)
                return ToolResult.Error("priorities 不能为空数组");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有殖民者。");

                    var pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到 ID={colonistId} 的殖民者。");

                    if (pawn.workSettings == null)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 没有工作设置。");

                    if (!Current.Game.playSettings.useWorkPriorities)
                        Current.Game.playSettings.useWorkPriorities = true;

                    var sb = new StringBuilder();
                    sb.AppendLine($"已更新 {pawn.Name.ToStringShort} 的工作优先级:");
                    var pawnShortName = pawn.Name.ToStringShort;

                    foreach (var (workTypeDefName, priority) in entries)
                    {
                        var workTypeDef = DefDatabase<WorkTypeDef>.GetNamed(workTypeDefName, errorOnFail: false);
                        if (workTypeDef == null)
                        {
                            sb.AppendLine($"- {workTypeDefName}: 未知工作类型，已跳过");
                            continue;
                        }

                        if (priority != 0 && pawn.WorkTypeIsDisabled(workTypeDef))
                        {
                            sb.AppendLine($"- {workTypeDef.labelShort ?? workTypeDef.defName}: 已禁用（年龄/能力限制），已跳过");
                            continue;
                        }

                        pawn.workSettings.SetPriority(workTypeDef, priority);
                        var label = priority switch
                        {
                            0 => "不分配",
                            1 => "最高(1)",
                            2 => "高(2)",
                            3 => "普通(3)",
                            4 => "最低(4)",
                            _ => $"{priority}"
                        };
                        sb.AppendLine($"- {workTypeDef.labelShort ?? workTypeDef.defName}: {label}");
                    }

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置工作优先级失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("colonist_id", out var jC) || !jC.TryGetInt32(out var id)) return null;
            var pawn = CameraHelper.FindPawnById(map, id);
            if (pawn == null) return null;
            return (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
        }
    }
}
