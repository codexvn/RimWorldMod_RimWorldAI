using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    /// <summary>更新任务状态或字段</summary>
    public class Tool_TaskUpdate : IInternalTool
    {
        public string Name => "task_update";
        public string Description => @"更新任务状态或字段。用于跟踪进度。

何时使用：
- 完成任务后标记为 completed
- 开始工作时标记为 in_progress
- 任务不再需要时设为 deleted 删除
- 需求变化时更新标题或描述

状态流转：pending → in_progress → completed。用 deleted 永久删除。

字段说明：
- status: 新状态（pending / in_progress / completed / deleted）
- subject: 新标题（可选）
- description: 新描述（可选）

示例：
  开始工作: task_update(taskId=""1"", status=""in_progress"")
  标记完成: task_update(taskId=""1"", status=""completed"")
  删除任务: task_update(taskId=""1"", status=""deleted"")

注意：更新前先用 task_get 确认任务最新状态。只在任务完全完成时才标记 completed。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                taskId = new { type = "string", description = "要更新的任务 ID（来自 task_create 或 task_list）" },
                subject = new { type = "string", description = "新标题（可选）" },
                description = new { type = "string", description = "新描述（可选）" },
                status = new { type = "string", description = "新状态: pending（待处理）, in_progress（进行中）, completed（已完成）, deleted（删除）" }
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

            // 提取可选参数
            var subject = args.Value.TryGetProperty("subject", out var s) && s.ValueKind != JsonValueKind.Null ? s.GetString() : null;
            var description = args.Value.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null ? d.GetString() : null;
            var status = args.Value.TryGetProperty("status", out var st) && st.ValueKind != JsonValueKind.Null ? st.GetString() : null;

            var wasDeleted = status == "deleted";
            var oldSubject = item.Subject;
            var oldStatus = item.Status;
            var updated = TaskStore.Update(taskId, subject, description, status);

            if (wasDeleted)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"已删除任务 #{taskId}: {item.Subject}");
                sb.AppendLine($"共 {TaskStore.PendingCount} 个未完成任务。");
                return Task.FromResult((sb.ToString(), false));
            }

            // 构建实际变更摘要（对比前后值）
            var changes = new List<string>();
            if (subject != null && oldSubject != subject) changes.Add($"标题 → \"{subject}\"");
            if (description != null) changes.Add("描述已更新");
            if (status != null && oldStatus != status) changes.Add($"状态 {oldStatus} → {status}");

            var result = new StringBuilder();
            result.AppendLine($"已更新任务 #{taskId}: {updated!.Subject}");
            if (changes.Count > 0)
            {
                foreach (var c in changes) result.AppendLine($"  - {c}");
            }
            else
            {
                result.AppendLine("  (未产生实际变更)");
            }
            result.AppendLine();
            result.AppendLine($"共 {TaskStore.PendingCount} 个未完成任务。");

            return Task.FromResult((result.ToString(), false));
        }
    }
}
