using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Verse;

namespace RimWorldAgent
{
    public static class BudgetStatus { public const string Ok = ""; public const string Warning = "Warning"; public const string Critical = "Critical"; public const string Exceeded = "Exceeded"; }
    public enum ChatRole { User, Assistant }
    public enum ChatState { Streaming, Done, Error }
    public enum ToolStatus { Running, Completed, Failed }

    public class ToolCallInfo
    {
        public string ItemId = "";
        public string Name = "";
        public string Title = "";
        public string Meta = "";
        public ToolStatus Status;
        public DateTime StartTime = DateTime.UtcNow;
        public double DurationMs;
    }

    public class ChatEntry
    {
        public ChatRole Role;
        public string Text = "";
        public string ThinkingText = "";
        public ChatState State;
        public string RunId = "";
        public string AgentId = "";
        public string AgentType = "";
        public bool IsContext;
        public int LastChunkLen;
        public float CachedHeight;
        public int CachedTextLen;
        public int CachedThinkingLen;
    }

    public static class ChatDisplayState
    {
        public static event Action? OnChanged;
        public static string CurrentBudgetStatus = "";
        public static float CurrentBudgetPercent;
        public static string CurrentBudgetText = "";

        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly object _lock = new();

        public static List<ChatEntry> Snapshot { get { lock (_lock) return _entries.ToList(); } }
        public static List<ToolCallInfo> ToolCallsSnapshot { get { lock (_lock) return _toolCalls.ToList(); } }

        public static void AddSystemMessage(string text) { lock (_lock) { _entries.Add(new ChatEntry { Role = ChatRole.Assistant, Text = text, State = ChatState.Done }); } OnChanged?.Invoke(); }
        public static void OnUserMessage(string text) { lock (_lock) { _entries.Add(new ChatEntry { Role = ChatRole.User, Text = text, State = ChatState.Done }); } OnChanged?.Invoke(); }
        public static void MarkLastAborted() { lock (_lock) { if (_entries.Count > 0 && _entries[_entries.Count - 1].State == ChatState.Streaming) _entries[_entries.Count - 1].State = ChatState.Error; } OnChanged?.Invoke(); }
        public static void Clear() { lock (_lock) { _entries.Clear(); _toolCalls.Clear(); } OnChanged?.Invoke(); }
    }

    public static class CCClient
    {
        public static bool IsConnected => false;
        public static bool IsReady => false;
        public static Task SendEventText(string evt, string cat, string text, object? stats = null) => Task.CompletedTask;
        public static Task SendAbort() => Task.CompletedTask;
    }

    public static class BridgeLifecycle
    {
        public static string DangerSummary => "";
    }

    public static class GameContextProvider
    {
        public static string BuildGameContext() => "";
        public static string BuildColonyOverview(Map map, List<Pawn> colonists, int count) => "";
    }

    public static class TokenUsageTracker
    {
        public static string GetCompactDisplay(long limit) => "Token --";
    }

    public static class ToolDisplayNames
    {
        public static string GetDisplayName(string rawName) => rawName;
    }
}
