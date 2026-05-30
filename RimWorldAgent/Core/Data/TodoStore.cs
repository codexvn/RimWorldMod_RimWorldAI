using System;
using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public static class TodoStore
    {
        public static ITodoStore Instance { get; set; } = new InMemoryTodoStore();
        public static Func<int>? TickProvider { get; set; }

        public static event Action? OnChanged
        {
            add => Instance.OnChanged += value;
            remove => Instance.OnChanged -= value;
        }

        public static int Count => Instance.Count;

        public static TodoItem Add(string description, int priority)
            => Instance.Add(description, priority);

        public static void AddExisting(TodoItem item)
            => Instance.AddExisting(item);

        public static bool Delete(string id)
            => Instance.Delete(id);

        public static bool UpdateStatus(string id, string newStatus)
            => Instance.UpdateStatus(id, newStatus);

        public static List<TodoItem> Query(string? statusFilter)
            => Instance.Query(statusFilter);

        public static void Clear()
            => Instance.Clear();
    }
}
