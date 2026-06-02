using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>获取指定任务的详细信息</summary>
    public class Tool_TaskGet : IInternalTool
    {
        public string Name => "task_get";
        public string Description => @"获取指定任务的完整详情，包括描述和状态。

何时使用：
- 开始工作前需要了解任务的完整要求
- 被分配任务后查看详细描述

输出：任务标题、描述、状态。
开始工作前用 task_list 查看所有任务的摘要。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                taskId = new { type = "string", description = "要查询的任务 ID" }
            },
            required = new[] { "taskId" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(("缺少参数：需要 taskId。", false));

            var taskId = args.Value.GetProperty("taskId").GetString()!;
            var item = TaskStore.Get(taskId);

            if (item == null)
                return Task.FromResult(($"未找到任务 #{taskId}。使用 task_list 查看所有任务。", false));

            var sb = new StringBuilder();
            sb.AppendLine($"任务 #{item.Id}: {item.Subject}");
            sb.AppendLine($"状态: {item.Status}");
            sb.AppendLine($"描述: {item.Description}");

            return Task.FromResult((sb.ToString(), false));
        }
    }
}
