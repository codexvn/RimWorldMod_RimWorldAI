using System.Collections.Generic;
using System.Linq;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>任务数据模型</summary>
    public class TaskItem
    {
        public string Id { get; set; } = "";
        /// <summary>任务标题，简洁明了（如"建造围墙防御区"）</summary>
        public string Subject { get; set; } = "";
        /// <summary>任务详细描述</summary>
        public string Description { get; set; } = "";
        /// <summary>pending / in_progress / completed</summary>
        public string Status { get; set; } = "pending";
    }

    /// <summary>线程安全的任务存储，供内部 task_* 工具和 ToolDispatcher 提醒共用</summary>
    public static class TaskStore
    {
        private static readonly List<TaskItem> _tasks = new();
        private static readonly object _lock = new();
        private static int _nextId = 1;

        /// <summary>未完成任务数</summary>
        public static int PendingCount
        {
            get { lock (_lock) return _tasks.Count(t => t.Status != "completed" && t.Status != "deleted"); }
        }

        /// <summary>总任务数</summary>
        public static int Count
        {
            get { lock (_lock) return _tasks.Count; }
        }

        /// <summary>创建任务，返回分配的任务</summary>
        public static TaskItem Create(string subject, string description)
        {
            TaskItem item;
            lock (_lock)
            {
                item = new TaskItem
                {
                    Id = (_nextId++).ToString(),
                    Subject = subject,
                    Description = description
                };
                _tasks.Add(item);
            }
            UIMessageBus.PushSdkTasks();
            return item;
        }

        /// <summary>更新任务。返回 null 表示未找到。status=deleted 时移除任务。</summary>
        public static TaskItem? Update(string taskId,
            string? subject = null, string? description = null, string? status = null)
        {
            bool changed = false;
            TaskItem? result;
            lock (_lock)
            {
                var item = _tasks.FirstOrDefault(t => t.Id == taskId);
                if (item == null) return null;

                if (status == "deleted")
                {
                    _tasks.Remove(item);
                    changed = true;
                    result = item;
                }
                else
                {
                    if (subject != null && item.Subject != subject) { item.Subject = subject; changed = true; }
                    if (description != null && item.Description != description) { item.Description = description; changed = true; }
                    if (status != null && item.Status != status) { item.Status = status; changed = true; }
                    result = item;
                }
            }
            if (changed) UIMessageBus.PushSdkTasks();
            return result;
        }

        /// <summary>获取单个任务</summary>
        public static TaskItem? Get(string taskId)
        {
            lock (_lock) return _tasks.FirstOrDefault(t => t.Id == taskId);
        }

        /// <summary>获取所有任务快照</summary>
        public static List<TaskItem> GetAll()
        {
            lock (_lock) return new List<TaskItem>(_tasks);
        }

        /// <summary>获取未完成任务快照</summary>
        public static List<TaskItem> GetPending()
        {
            lock (_lock) return _tasks.Where(t => t.Status != "completed" && t.Status != "deleted").ToList();
        }

        /// <summary>清空全部任务（新会话开始时调用）</summary>
        public static void Clear()
        {
            int count;
            lock (_lock) { count = _tasks.Count; _tasks.Clear(); _nextId = 1; }
            if (count > 0) UIMessageBus.PushSdkTasks();
        }
    }
}
