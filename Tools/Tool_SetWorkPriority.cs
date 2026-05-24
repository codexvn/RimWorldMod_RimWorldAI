using System;
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
        public string Description => "设置殖民者的工作优先级 (0-4)。0=不分配，1=最高优先，4=最低优先。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_name = new { type = "string", description = "殖民者名称" },
                work_type = new { type = "string", description = "工作类型 defName", @enum = new[] { "Crafting", "Cooking", "Construction", "Mining", "Growing", "Research", "Smithing", "Tailoring", "Hauling", "Cleaning", "Warden", "Hunting", "Art", "PlantCutting", "Doctor", "Patient", "Firefighter" } },
                priority = new { type = "integer", description = "优先级: 0=不分配, 1=最高, 2=高, 3=普通, 4=最低", minimum = 0, maximum = 4 }
            },
            required = new[] { "colonist_name", "work_type", "priority" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_name", out var cn))
                return ToolResult.Error("缺少 colonist_name");
            if (!args.Value.TryGetProperty("work_type", out var wt))
                return ToolResult.Error("缺少 work_type");
            if (!args.Value.TryGetProperty("priority", out var p) || !p.TryGetInt32(out var priority))
                return ToolResult.Error("缺少有效的 priority");
            if (priority < 0 || priority > 4)
                return ToolResult.Error("priority 必须在 0-4 之间。0=不分配, 1=最高优先, 4=最低优先。");

            var colonistName = cn.GetString() ?? "";
            var workTypeDefName = wt.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(colonistName))
                return ToolResult.Error("colonist_name 不能为空。");
            if (string.IsNullOrWhiteSpace(workTypeDefName))
                return ToolResult.Error("work_type 不能为空。");

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    // 查找殖民者（模糊匹配）
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有殖民者。");

                    var pawn = colonists.FirstOrDefault(c =>
                        c.Name.ToStringShort.IndexOf(colonistName, StringComparison.OrdinalIgnoreCase) >= 0
                        || c.Name.ToStringFull.IndexOf(colonistName, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (pawn == null)
                        return ToolResult.Error($"未找到匹配的殖民者: {colonistName}。使用 get_colonists 查看可用殖民者。");

                    // 检查 workSettings 可用性
                    if (pawn.workSettings == null)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 没有工作设置（可能不是殖民者）。");

                    // 查找工作类型
                    var workTypeDef = DefDatabase<WorkTypeDef>.GetNamed(workTypeDefName, errorOnFail: false);
                    if (workTypeDef == null)
                        return ToolResult.Error($"未知工作类型: {workTypeDefName}。请使用 get_colonists 查看可用工作类型。");

                    var pawnShortName = pawn.Name.ToStringShort;
                    var workLabel = workTypeDef.labelShort ?? workTypeDef.label ?? workTypeDefName;
                    var sb = new StringBuilder();

                    // 自动开启自定义优先级（等同 UI 勾选"手动优先级"复选框）
                    if (!Current.Game.playSettings.useWorkPriorities)
                    {
                        Current.Game.playSettings.useWorkPriorities = true;
                        sb.AppendLine("已自动开启手动工作优先级。");
                    }

                    // 执行设置
                    if (priority != 0 && pawn.WorkTypeIsDisabled(workTypeDef))
                        return ToolResult.Error($"{pawnShortName} 无法执行 {workLabel} 工作（被年龄或能力限制禁用）。");

                    pawn.workSettings.SetPriority(workTypeDef, priority);

                    var priorityText = priority switch
                    {
                        0 => "不分配",
                        1 => "最高优先 (1)",
                        2 => "高优先 (2)",
                        3 => "普通 (3)",
                        4 => "最低优先 (4)",
                        _ => $"优先级 {priority}"
                    };
                    sb.AppendLine($"已更新 {pawnShortName} 的工作优先级:");
                    sb.AppendLine($"- 工作: {workLabel} ({workTypeDefName})");
                    sb.AppendLine($"- 优先级: {priorityText}");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"设置工作优先级失败: {ex.Message}");
                }
            });
        }
    }
}
