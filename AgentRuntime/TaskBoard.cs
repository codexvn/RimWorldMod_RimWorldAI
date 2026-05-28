using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Verse;

namespace RimWorldMCP.AgentRuntime
{
    public enum TaskState { Queued, Running, Blocked, Completed, Failed }

    public class TaskEntry
    {
        public int Id { get; set; }
        public string Goal { get; set; } = "";
        public string Agent { get; set; } = "";
        public TaskState State { get; set; } = TaskState.Queued;
        public string Progress { get; set; } = "-";
        public long CreatedTick { get; set; }
    }

    /// <summary>
    /// 跨 Agent 共享的任务面板，JSON 持久化到 session 目录。
    /// </summary>
    public static class TaskBoard
    {
        private static List<TaskEntry> _tasks = new();
        private static int _nextId = 1;
        private static string _sessionDir = "";
        private static readonly object _lock = new();

        public static string SessionDir
        {
            get => _sessionDir;
            set { _sessionDir = value; Load(); }
        }

        public static IReadOnlyList<TaskEntry> AllTasks => _tasks.AsReadOnly();

        public static int Add(string goal, string agent)
        {
            lock (_lock)
            {
                var task = new TaskEntry
                {
                    Id = _nextId++,
                    Goal = goal,
                    Agent = agent,
                    State = TaskState.Queued,
                    CreatedTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0
                };
                _tasks.Add(task);
                Save();
                return task.Id;
            }
        }

        public static void Remove(int id)
        {
            lock (_lock)
            {
                _tasks.RemoveAll(t => t.Id == id);
                Save();
            }
        }

        public static void UpdateState(int id, TaskState state, string? progress = null)
        {
            lock (_lock)
            {
                var task = _tasks.FirstOrDefault(t => t.Id == id);
                if (task == null) return;
                task.State = state;
                if (progress != null) task.Progress = progress;
                Save();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _tasks.Clear();
                _nextId = 1;
                Save();
            }
        }

        /// <summary>
        /// 生成 Markdown 格式的任务面板，直接注入 Prompt。
        /// </summary>
        public static string ToMarkdown()
        {
            lock (_lock)
            {
                if (_tasks.Count == 0) return "（无活跃任务）";
                var lines = new List<string>
                {
                    "## 任务面板",
                    "| ID | 目标 | Agent | 状态 | 进度 |",
                    "|----|------|-------|------|------|"
                };
                foreach (var t in _tasks)
                    lines.Add($"| {t.Id} | {t.Goal} | {t.Agent} | {t.State.ToString().ToLower()} | {t.Progress} |");
                return string.Join("\n", lines);
            }
        }

        private static string FilePath =>
            string.IsNullOrEmpty(_sessionDir) ? "" : Path.Combine(_sessionDir, "taskboard.json");

        private static void Save()
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var data = new { tasks = _tasks, nextId = _nextId };
                File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                McpLog.Error($"[TaskBoard] 保存失败: {ex.Message}");
            }
        }

        private static void Load()
        {
            var path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<SaveData>(json);
                if (data != null)
                {
                    _tasks = data.tasks ?? new List<TaskEntry>();
                    _nextId = data.nextId;
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"[TaskBoard] 加载失败: {ex.Message}");
                _tasks = new List<TaskEntry>();
                _nextId = 1;
            }
        }

        private class SaveData
        {
            public List<TaskEntry>? tasks { get; set; }
            public int nextId { get; set; }
        }
    }
}
