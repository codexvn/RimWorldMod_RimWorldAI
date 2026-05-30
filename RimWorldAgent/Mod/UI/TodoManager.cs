using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimWorldAgent
{
    public class TodoItem : IExposable
    {
        public string Id = "";
        public string Description = "";
        public int Priority = 3;
        public string Status = "pending";
        public int CreatedAtTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id", "");
            Scribe_Values.Look(ref Description, "desc", "");
            Scribe_Values.Look(ref Priority, "prio", 3);
            Scribe_Values.Look(ref Status, "status", "pending");
            Scribe_Values.Look(ref CreatedAtTick, "createdAtTick", 0);
        }
    }

    public static class TodoManager
    {
        private static List<TodoItem> _items = new();
        private static readonly object _lock = new();

        public static event Action? OnChanged;

        public static int Count { get { lock (_lock) return _items.Count; } }

        public static TodoItem Add(string description, int priority)
        {
            TodoItem item;
            lock (_lock)
            {
                item = new TodoItem
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Description = description,
                    Priority = priority,
                    Status = "pending",
                    CreatedAtTick = Find.TickManager?.TicksAbs ?? 0
                };
                _items.Add(item);
            }
            OnChanged?.Invoke();
            return item;
        }

        public static bool Delete(string id)
        {
            bool removed;
            lock (_lock) { removed = _items.RemoveAll(i => i.Id == id) > 0; }
            if (removed) OnChanged?.Invoke();
            return removed;
        }

        public static bool UpdateStatus(string id, string newStatus)
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

        public static List<TodoItem> Query(string? statusFilter)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(statusFilter)) return _items.ToList();
                return _items.Where(i => i.Status == statusFilter).ToList();
            }
        }

        public static void Clear()
        {
            lock (_lock) { _items.Clear(); }
            OnChanged?.Invoke();
        }

        public static void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                var count = _items.Count;
                Scribe_Values.Look(ref count, "todoCount", 0);
                for (int i = 0; i < count; i++)
                    _items[i].ExposeData();
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                _items.Clear();
                int count = 0;
                Scribe_Values.Look(ref count, "todoCount", 0);
                for (int i = 0; i < count; i++)
                {
                    var item = new TodoItem();
                    item.ExposeData();
                    _items.Add(item);
                }
            }
        }
    }
}
