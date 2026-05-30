using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldAgent.Core.Data
{
    public class InMemoryTodoStore : ITodoStore
    {
        private readonly List<TodoItem> _items = new();
        private readonly object _lock = new();

        public event Action? OnChanged;

        public int Count { get { lock (_lock) return _items.Count; } }

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
            OnChanged?.Invoke();
            return item;
        }

        public void AddExisting(TodoItem item)
        {
            lock (_lock) { _items.Add(item); }
        }

        public bool Delete(string id)
        {
            bool removed;
            lock (_lock) { removed = _items.RemoveAll(i => i.Id == id) > 0; }
            if (removed) OnChanged?.Invoke();
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
            if (found) OnChanged?.Invoke();
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
            OnChanged?.Invoke();
        }
    }
}
