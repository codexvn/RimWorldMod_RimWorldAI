using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>创建新任务</summary>
    public class Tool_TaskCreate : IInternalTool
    {
        public string Name => "task_create";
        public string Description => @"创建新任务跟踪执行进度。用于组织复杂工作、跟踪进度。

何时使用：
- 复杂多步骤任务（3+ 步）
- 非平凡的重要任务需要仔细规划
- PLAN 阶段制定计划时
- 收到新指令后立即捕获为任务
- 开始工作时标记进度，完成后标注状态

何时不用：
- 单一步骤直接完成的任务
- 纯对话或信息查询
- 少于 3 个步骤的简单操作

字段说明：
- subject: 任务标题，简洁明了（如'建造围墙防御区'）
- description: 需要做什么的详细描述

所有任务创建时状态为 pending。用 task_update 标记完成。创建前先用 task_list 确认没有重复任务。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                subject = new { type = "string", description = "任务标题，简洁明了（如'建造围墙防御区'）" },
                description = new { type = "string", description = "任务详细描述，说明要做什么" }
            },
            required = new[] { "subject", "description" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(("缺少参数：需要 subject, description。", false));

            var subject = args.Value.GetProperty("subject").GetString()!;
            var description = args.Value.GetProperty("description").GetString()!;

            var item = TaskStore.Create(subject, description);

            var sb = new StringBuilder();
            sb.AppendLine($"已创建任务 #{item.Id}: {item.Subject}");
            sb.AppendLine($"共 {TaskStore.PendingCount} 个未完成任务。");

            return Task.FromResult((sb.ToString(), false));
        }
    }
}
