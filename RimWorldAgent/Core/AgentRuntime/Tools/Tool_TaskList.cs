using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>列出所有任务及其状态</summary>
    public class Tool_TaskList : IInternalTool
    {
        public string Name => "task_list";
        public string Description => @"列出所有任务及其状态。用于查看进度、找未完成任务。

何时使用：
- 查看有哪些待处理任务
- 检查整体进度
- 完成后找下一个可用任务

输出摘要：任务 ID、标题、状态（pending / in_progress / completed）。
用 task_get(taskId) 查看指定任务的完整描述。
优先处理 ID 较小的任务。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var all = TaskStore.GetAll();
            if (all.Count == 0)
                return Task.FromResult(("当前没有任务。使用 task_create 创建新任务。", false));

            var sb = new StringBuilder();
            sb.AppendLine($"共 {all.Count} 个任务：");
            sb.AppendLine();

            foreach (var t in all)
            {
                var statusIcon = t.Status switch
                {
                    "pending" => "[ ]",
                    "in_progress" => "[>]",
                    "completed" => "[✓]",
                    _ => "[?]"
                };
                sb.AppendLine($"{statusIcon} #{t.Id} {t.Subject} ({t.Status})");
            }

            var pending = TaskStore.PendingCount;
            if (pending > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{pending} 个未完成。");
            }

            return Task.FromResult((sb.ToString(), false));
        }
    }
}
