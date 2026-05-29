using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
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

        public static int Count
        {
            get { lock (_lock) return _items.Count; }
        }

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
                    CreatedAtTick = Find.TickManager.TicksAbs
                };
                _items.Add(item);
            }
            OnChanged?.Invoke();
            PushToCompanion();
            return item;
        }

        public static bool Delete(string id)
        {
            bool removed;
            lock (_lock) { removed = _items.RemoveAll(i => i.Id == id) > 0; }
            if (removed)
            {
                OnChanged?.Invoke();
                PushToCompanion();
            }
            return removed;
        }

        public static bool UpdateStatus(string id, string newStatus)
        {
            // done/cancelled 直接删除，避免堆积
            if (newStatus == "done" || newStatus == "cancelled")
                return Delete(id);

            bool found;
            lock (_lock)
            {
                var item = _items.Find(i => i.Id == id);
                if (item == null) found = false;
                else { item.Status = newStatus; found = true; }
            }
            if (found)
            {
                OnChanged?.Invoke();
                PushToCompanion();
            }
            return found;
        }

        public static List<TodoItem> Query(string? filter)
        {
            lock (_lock)
            {
                var list = new List<TodoItem>(_items);
                if (filter == "pending")
                    list.RemoveAll(i => i.Status != "pending");
                else if (filter == "done")
                    list.RemoveAll(i => i.Status != "done");
                else if (filter == "cancelled")
                    list.RemoveAll(i => i.Status != "cancelled");

                list.Sort((a, b) =>
                {
                    int p = b.Priority.CompareTo(a.Priority);
                    if (p != 0) return p;
                    return a.CreatedAtTick.CompareTo(b.CreatedAtTick);
                });
                return list;
            }
        }

        public static void Clear()
        {
            bool hadItems;
            lock (_lock)
            {
                hadItems = _items.Count > 0;
                _items.Clear();
            }
            if (hadItems)
            {
                OnChanged?.Invoke();
                PushToCompanion();
            }
        }

        public static void ExposeData()
        {
            // Scribe_Collections.Look 在 LookMode.Deep 下要求元素实现 IExposable
            Scribe_Collections.Look(ref _items, "todoItems", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && _items == null)
                _items = new List<TodoItem>();
        }

        internal static object BuildCompanionPayload()
        {
            var items = Query(null);
            return new
            {
                todoItems = items.ConvertAll(i => new
                {
                    id = i.Id,
                    description = i.Description,
                    priority = i.Priority,
                    status = i.Status,
                    createdAtTick = i.CreatedAtTick,
                    createdAtStr = Helpers.GameTimeHelper.FormatGameTime(i.CreatedAtTick)
                })
            };
        }

        private static void PushToCompanion()
        {
            _ = CCClient.SendEvent("todo-state", BuildCompanionPayload());
        }
    }
}
