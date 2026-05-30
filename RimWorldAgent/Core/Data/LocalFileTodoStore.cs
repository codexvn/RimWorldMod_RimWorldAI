using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>本地 JSON 文件持久化的 TODO 存储。EXE 模式或需要跨进程持久化时使用。</summary>
    public class LocalFileTodoStore : ITodoStore
    {
        private readonly object _lock = new();
        private List<TodoItem> _items = new();
        private readonly string _filePath;

        public event Action? OnChanged;

        public int Count { get { lock (_lock) return _items.Count; } }

        public LocalFileTodoStore(string? filePath = null)
        {
            _filePath = filePath ?? GetDefaultPath("RimWorldMCP_Todos.json");
            Load();
        }

        public TodoItem Add(string description, int priority)
        {
            var item = new TodoItem
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Description = description,
                Priority = priority,
                Status = "pending",
                CreatedAtTick = TodoStore.TickProvider?.Invoke() ?? 0
            };
            lock (_lock) { _items.Add(item); }
            Save();
            OnChanged?.Invoke();
            return item;
        }

        public void AddExisting(TodoItem item)
        {
            lock (_lock) { _items.Add(item); }
            Save();
        }

        public bool Delete(string id)
        {
            bool removed;
            lock (_lock) { removed = _items.RemoveAll(i => i.Id == id) > 0; }
            if (removed)
            {
                Save();
                OnChanged?.Invoke();
            }
            return removed;
        }

        public bool UpdateStatus(string id, string newStatus)
        {
            bool found;
            lock (_lock)
            {
                var item = _items.Find(i => i.Id == id);
                if (item == null) found = false;
                else { item.Status = newStatus; found = true; }
            }
            if (found)
            {
                Save();
                OnChanged?.Invoke();
            }
            return found;
        }

        public List<TodoItem> Query(string? statusFilter)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(statusFilter)) return _items.ToList();
                return _items.Where(i => i.Status == statusFilter).ToList();
            }
        }

        public void Clear()
        {
            lock (_lock) { _items.Clear(); }
            Save();
            OnChanged?.Invoke();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _items = JsonSerializer.Deserialize<List<TodoItem>>(json) ?? new List<TodoItem>();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LocalFileTodo] 加载失败: {ex.Message}");
                _items = new List<TodoItem>();
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    File.WriteAllText(_filePath, json);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LocalFileTodo] 保存失败: {ex.Message}");
                }
            }
        }

        private static string GetDefaultPath(string fileName)
        {
            var dir = TaskBoard.SessionDir;
            if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, fileName);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RimWorldMCP", fileName);
        }
    }
}
