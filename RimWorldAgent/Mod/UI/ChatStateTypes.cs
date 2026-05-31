using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using Verse;

namespace RimWorldAgent
{
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
        public static BudgetStatus CurrentBudgetStatus = BudgetStatus.Ok;
        public static float CurrentBudgetPercent;
        public static string CurrentBudgetText = "";

        private static readonly List<ChatEntry> _entries = new();
        private static readonly List<ToolCallInfo> _toolCalls = new();
        private static readonly object _lock = new();

        public static List<ChatEntry> Snapshot { get { lock (_lock) return _entries.ToList(); } }
        public static List<ToolCallInfo> ToolCallsSnapshot { get { lock (_lock) return _toolCalls.ToList(); } }

        public static void AddSystemMessage(string text) { lock (_lock) { _entries.Add(new ChatEntry { Role = ChatRole.Assistant, Text = text, State = ChatState.Done }); } OnChanged?.Invoke(); }
        public static void OnUserMessage(string text) { lock (_lock) { _entries.Add(new ChatEntry { Role = ChatRole.User, Text = text, State = ChatState.Done }); } OnChanged?.Invoke(); }

        public static void MarkLastAborted()
        {
            lock (_lock)
            {
                if (_entries.Count > 0 && _entries[_entries.Count - 1].State == ChatState.Streaming)
                    _entries[_entries.Count - 1].State = ChatState.Error;
            }
            OnChanged?.Invoke();
        }

        public static void Clear()
        {
            lock (_lock) { _entries.Clear(); _toolCalls.Clear(); }
            OnChanged?.Invoke();
        }

        // ===== 从 CcbWebSocket 事件填充 =====

        /// <summary>流式文本追加（AI 思考/正文），自动创建或追加 Streaming 条目</summary>
        public static void OnAssistantText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                var last = _entries.Count > 0 ? _entries[_entries.Count - 1] : null;
                if (last == null || last.State != ChatState.Streaming || last.Role != ChatRole.Assistant)
                {
                    last = new ChatEntry { Role = ChatRole.Assistant, State = ChatState.Streaming };
                    _entries.Add(last);
                }
                last.Text += text;
            }
            OnChanged?.Invoke();
        }

        /// <summary>工具开始执行</summary>
        public static void AddToolCall(string toolId, string toolName, JsonElement? input)
        {
            lock (_toolCalls)
            {
                _toolCalls.Add(new ToolCallInfo
                {
                    ItemId = toolId,
                    Name = toolName.Replace("mcp__agent__", "").Replace("mcp__rimworld__", ""),
                    Meta = input?.ToString() ?? "",
                    Status = ToolStatus.Running,
                });
            }
            OnChanged?.Invoke();
        }

        /// <summary>工具执行完成（按 toolId 匹配最近一个 running）</summary>
        public static void FinishToolCall(string toolId, bool isError, double durationMs)
        {
            lock (_toolCalls)
            {
                for (int i = _toolCalls.Count - 1; i >= 0; i--)
                {
                    if (_toolCalls[i].ItemId == toolId && _toolCalls[i].Status == ToolStatus.Running)
                    {
                        _toolCalls[i].Status = isError ? ToolStatus.Failed : ToolStatus.Completed;
                        _toolCalls[i].DurationMs = durationMs;
                        break;
                    }
                }
            }
            OnChanged?.Invoke();
        }

        /// <summary>流式结束，最后一条 Streaming → Done</summary>
        public static void FinishStreaming()
        {
            lock (_lock)
            {
                if (_entries.Count > 0 && _entries[_entries.Count - 1].State == ChatState.Streaming)
                    _entries[_entries.Count - 1].State = ChatState.Done;
            }
            OnChanged?.Invoke();
        }
    }

    /// <summary>CcbWebSocket 桥接，由 GameComponent 注入</summary>
    public static class CCClient
    {
        private static CcbWebSocket? _ws;
        public static bool IsConnected => _ws?.IsConnected ?? false;
        public static bool IsReady => _ws?.IsReady ?? false;

        public static void SetSocket(CcbWebSocket ws) => _ws = ws;

        public static async Task SendEventText(string evt, string cat, string text, object? stats = null)
        {
            if (_ws != null)
            {
                await _ws.SendEvent(evt, new { category = cat, text, stats });
            }
        }

        public static async Task SendAbort()
        {
            if (_ws != null) await _ws.SendAbort();
        }
    }

    /// <summary>威胁摘要（由 Agent 通过 MCP 更新）</summary>
    public static class BridgeLifecycle
    {
        public static string DangerSummary = "";
    }

    /// <summary>殖民地概览生成（通过 MCP get_game_context 获取）</summary>
    public static class GameContextProvider
    {
        public static string BuildGameContext() => "";
        public static string BuildColonyOverview(Map map, List<Pawn> colonists, int count) => "";
    }

    /// <summary>工具显示名称转换</summary>
    public static class ToolDisplayNames
    {
        public static string GetDisplayName(string rawName) => rawName;
    }
}
