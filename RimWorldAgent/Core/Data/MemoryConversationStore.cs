using System;
using System.Collections.Generic;
using System.Linq;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// 纯内存会话存储 — MOD 模式使用。
    /// 游戏重载即清空。
    /// </summary>
    public sealed class MemoryConversationStore : IConversationStore
    {
        private readonly List<ConversationEntry> _entries = new();
        private readonly object _lock = new();
        private long _nextId = 1;

        public int Count
        {
            get { lock (_lock) return _entries.Count; }
        }

        public void RecordUserMessage(string text)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.User,
                Text = text ?? "",
                Timestamp = DateTime.UtcNow,
                GameDay = AgentOrchestrator.GameDay
            };
            lock (_lock) _entries.Add(entry);
        }

        public void RecordAssistantMessage(string text, string thinking, string runId, string agentType)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.Assistant,
                Text = text ?? "",
                Thinking = thinking ?? "",
                RunId = runId ?? "",
                AgentType = agentType ?? "",
                Timestamp = DateTime.UtcNow,
                GameDay = AgentOrchestrator.GameDay
            };
            lock (_lock) _entries.Add(entry);
        }

        public void RecordSystemMessage(string text)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.System,
                Text = text ?? "",
                Timestamp = DateTime.UtcNow,
                GameDay = AgentOrchestrator.GameDay
            };
            lock (_lock) _entries.Add(entry);
        }

        public void RecordToolCall(string toolId, string name, string input, string permissionToolName = "")
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.ToolCall,
                RunId = toolId ?? "",
                ToolName = name ?? "",
                ToolInput = input ?? "",
                PermissionToolName = permissionToolName ?? "",
                Timestamp = DateTime.UtcNow,
                GameDay = AgentOrchestrator.GameDay
            };
            lock (_lock) _entries.Add(entry);
        }

        public string? GetPermissionToolName(string toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId)) return null;
            lock (_lock)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    var e = _entries[i];
                    if (e.Role == ConvRole.ToolCall && e.RunId == toolCallId)
                        return string.IsNullOrEmpty(e.PermissionToolName) ? null : e.PermissionToolName;
                }
            }
            return null;
        }

        public void RecordToolResult(string toolId, bool isError, double durationMs, string output)
        {
            var entry = new ConversationEntry
            {
                Id = GetNextId(),
                Role = ConvRole.ToolResult,
                RunId = toolId ?? "",
                Text = output ?? "",
                IsToolError = isError,
                ToolDurationMs = durationMs,
                Timestamp = DateTime.UtcNow,
                GameDay = AgentOrchestrator.GameDay
            };
            lock (_lock) _entries.Add(entry);
        }

        public ConversationEntry? GetAt(long id)
        {
            lock (_lock)
            {
                return _entries.FirstOrDefault(e => e.Id == id);
            }
        }

        public IReadOnlyList<ConversationEntry> GetRecent(int n)
        {
            lock (_lock)
            {
                var start = Math.Max(0, _entries.Count - n);
                return _entries.Skip(start).ToList();
            }
        }

        public IReadOnlyList<ConversationEntry> GetBefore(long beforeId, int n)
        {
            lock (_lock)
            {
                // 找到 beforeId 的位置，取其之前的 n 条
                var idx = _entries.FindIndex(e => e.Id == beforeId);
                if (idx <= 0) return new List<ConversationEntry>();
                var start = Math.Max(0, idx - n);
                return _entries.GetRange(start, idx - start);
            }
        }

        public IReadOnlyList<ConversationEntry> QueryToolCalls(
            string? toolName = null, int fromDay = 0, int toDay = int.MaxValue,
            int limit = 100, long beforeId = long.MaxValue)
        {
            lock (_lock)
            {
                var query = _entries.Where(e =>
                    e.Role == ConvRole.ToolCall
                    && e.Id < beforeId
                    && (fromDay == 0 || e.GameDay >= fromDay)
                    && (toDay == int.MaxValue || e.GameDay <= toDay));
                if (toolName != null)
                    query = query.Where(e => e.ToolName == toolName);
                return query.OrderByDescending(e => e.Id).Take(limit).Reverse().ToList();
            }
        }

        public IReadOnlyList<ToolCallDailyStat> GetToolDailyStats(
            int fromDay = 0, int toDay = int.MaxValue)
        {
            lock (_lock)
            {
                return _entries
                    .Where(e => e.Role == ConvRole.ToolCall
                        && (fromDay == 0 || e.GameDay >= fromDay)
                        && (toDay == int.MaxValue || e.GameDay <= toDay))
                    .GroupBy(e => new { e.GameDay, e.ToolName })
                    .Select(g => new ToolCallDailyStat
                    {
                        GameDay = g.Key.GameDay,
                        ToolName = g.Key.ToolName,
                        CallCount = g.Count()
                    })
                    .OrderByDescending(s => s.GameDay)
                    .ThenByDescending(s => s.CallCount)
                    .ToList();
            }
        }

        public List<string> GetKnownToolNames()
        {
            lock (_lock)
            {
                return _entries
                    .Where(e => e.Role == ConvRole.ToolCall && !string.IsNullOrEmpty(e.ToolName))
                    .Select(e => e.ToolName!)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
        }

        private long GetNextId()
        {
            lock (_lock) return _nextId++;
        }
    }
}
