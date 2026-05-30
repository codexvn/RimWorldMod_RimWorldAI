using System;
using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    public interface ITodoStore
    {
        TodoItem Add(string description, int priority);
        void AddExisting(TodoItem item);
        bool Delete(string id);
        bool UpdateStatus(string id, string newStatus);
        List<TodoItem> Query(string? statusFilter);
        void Clear();
        int Count { get; }
        event Action? OnChanged;
    }
}
