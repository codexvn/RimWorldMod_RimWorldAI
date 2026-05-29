using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimWorldMCP.Tools
{
    public class MemoryEntry
    {
        public string Id { get; set; } = "";
        public int Priority { get; set; }
        public string Content { get; set; } = "";
        public string Timestamp { get; set; } = "";
    }

    public static class MemoryManager
    {
        private static readonly object _lock = new();
        private static string? _filePath;

        private static string GetFilePath()
        {
            if (_filePath != null) return _filePath;

            try
            {
                // CCB --project-path 同目录（companionDir/../claude-sessions/）
                var dir = BridgeLifecycle.FindCompanionDir();
                if (!string.IsNullOrEmpty(dir))
                {
                    _filePath = Path.GetFullPath(Path.Combine(dir, "..", "claude-sessions", "memory.json"));
                    return _filePath;
                }
            }
            catch { }

            try
            {
                var asmPath = typeof(MemoryManager).Assembly.Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (asmDir != null)
                    {
                        _filePath = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "memory.json"));
                        return _filePath;
                    }
                }
            }
            catch { }

            _filePath = "memory.json";
            return _filePath;
        }

        private static List<MemoryEntry> Load()
        {
            var path = GetFilePath();
            if (!File.Exists(path)) return new List<MemoryEntry>();

            try
            {
                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<MemoryEntry>>(json);
                return entries ?? new List<MemoryEntry>();
            }
            catch
            {
                return new List<MemoryEntry>();
            }
        }

        private static void Save(List<MemoryEntry> entries)
        {
            var path = GetFilePath();
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(path, json);
        }

        private static string GenerateId()
        {
            return $"mem-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N").Substring(0, 4)}";
        }

        public static MemoryEntry Add(int priority, string content)
        {
            var entry = new MemoryEntry
            {
                Id = GenerateId(),
                Priority = priority,
                Content = content,
                Timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")
            };

            lock (_lock)
            {
                var entries = Load();
                entries.Add(entry);
                SortAndSave(entries);
            }

            return entry;
        }

        public static List<MemoryEntry> ListAll()
        {
            lock (_lock)
            {
                var entries = Load();
                SortEntries(entries);
                return entries;
            }
        }

        public static bool Delete(string id)
        {
            lock (_lock)
            {
                var entries = Load();
                var removed = entries.RemoveAll(e => e.Id == id);
                if (removed > 0)
                {
                    Save(entries);
                    return true;
                }
                return false;
            }
        }

        public static bool Update(string id, int? priority, string? content)
        {
            lock (_lock)
            {
                var entries = Load();
                var entry = entries.Find(e => e.Id == id);
                if (entry == null) return false;

                if (priority.HasValue)
                    entry.Priority = priority.Value;
                if (content != null)
                    entry.Content = content;

                SortAndSave(entries);
                return true;
            }
        }

        private static void SortEntries(List<MemoryEntry> entries)
        {
            entries.Sort((a, b) =>
            {
                int p = b.Priority.CompareTo(a.Priority);
                if (p != 0) return p;
                return string.Compare(b.Timestamp, a.Timestamp, StringComparison.Ordinal);
            });
        }

        private static void SortAndSave(List<MemoryEntry> entries)
        {
            SortEntries(entries);
            Save(entries);
        }
    }
}
