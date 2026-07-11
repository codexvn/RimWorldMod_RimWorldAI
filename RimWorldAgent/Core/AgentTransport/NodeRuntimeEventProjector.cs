using System;
using System.Collections.Concurrent;
using System.Text.Json;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.Core.AgentTransport
{
    internal sealed class NodeRuntimeEventProjector
    {
        private readonly string? _modelName;
        private readonly Action<string> _logWarn;
        private readonly Action _onActivity;
        private readonly Action<string, string?> _onResult;
        private readonly Action<string, string, string> _onToolUse;
        private readonly Action _onAborted;
        private readonly ConcurrentDictionary<string, bool> _startedTools = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, long> _toolStartedTicks = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, string> _assistantChunks = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, string> _thoughtChunks = new ConcurrentDictionary<string, string>();

        public NodeRuntimeEventProjector(string? modelName, Action<string> logWarn, Action onActivity,
            Action<string, string?> onResult, Action<string, string, string> onToolUse, Action onAborted)
        {
            _modelName = modelName;
            _logWarn = logWarn;
            _onActivity = onActivity;
            _onResult = onResult;
            _onToolUse = onToolUse;
            _onAborted = onAborted;
        }

        public void Project(AgentEvent evt)
        {
            _onActivity();
            try
            {
                var sessionId = evt.SessionId;
                if (!string.IsNullOrWhiteSpace(sessionId) && AgentLoop.AgentSessionId != sessionId)
                {
                    var safeSessionId = sessionId!;
                    AgentLoop.AgentSessionId = safeSessionId;
                    AgentLoop.RaiseSessionIdChanged(safeSessionId);
                    Push(UiMessage.SystemInit(_modelName, safeSessionId, null, "acp"));
                }

                switch (evt.Kind)
                {
                    case "text_delta":
                        ProjectText(evt.MessageId, evt.Text, _assistantChunks, false);
                        break;
                    case "thought_delta":
                        ProjectText(evt.MessageId, evt.Text, _thoughtChunks, true);
                        break;
                    case "user_message":
                        if (!string.IsNullOrEmpty(evt.Text)) Push(UiMessage.User(evt.Text!));
                        break;
                    case "session_info":
                    case "status":
                        if (!string.IsNullOrWhiteSpace(evt.TitleText)) Push(UiMessage.System(evt.TitleText!));
                        break;
                    case "tool_call":
                        ProjectTool(evt, false);
                        break;
                    case "tool_update":
                    case "tool_result":
                        ProjectTool(evt, true);
                        break;
                    case "usage":
                        ProjectUsage(evt);
                        break;
                    case "aborted":
                        ProjectCancelled();
                        break;
                }
            }
            catch (Exception ex)
            {
                _logWarn("[NodeACP] runtime event projection failed: " + FormatExceptionChain(ex));
            }
        }

        public void ProjectPromptResponse(PromptResponse response, long durationMs)
        {
            _onActivity();
            RecordUsage(response, durationMs);
            var stopReason = response.StopReason ?? "";
            if (string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stopReason, "canceled", StringComparison.OrdinalIgnoreCase))
            {
                ProjectCancelled();
                return;
            }
            Push(UiMessage.Result("success", stopReason));
            _onResult("success", stopReason);
        }

        private void RecordUsage(PromptResponse response, long durationMs)
        {
            if (!response.InputTokens.HasValue && !response.OutputTokens.HasValue &&
                !response.CacheReadTokens.HasValue && !response.CacheCreateTokens.HasValue)
                return;
            var input = response.InputTokens ?? 0;
            var output = response.OutputTokens ?? 0;
            var cacheRead = response.CacheReadTokens ?? 0;
            var cacheCreate = response.CacheCreateTokens ?? 0;
            TokenUsageTracker.CurrentInputTokens = input;
            TokenUsageTracker.CurrentCacheReadTokens = cacheRead;
            TokenUsageTracker.CurrentCacheCreateTokens = cacheCreate;
            TokenUsageTracker.Record(_modelName ?? "acp", input, output, cacheRead, cacheCreate, durationMs);
        }

        public void ProjectCancelled()
        {
            Push(UiMessage.Aborted());
            _onAborted();
        }

        private void ProjectText(string? messageId, string? text, ConcurrentDictionary<string, string> chunks, bool thinking)
        {
            if (string.IsNullOrEmpty(text)) return;
            var key = string.IsNullOrEmpty(messageId) ? Guid.NewGuid().ToString("N") : messageId!;
            chunks.AddOrUpdate(key, text!, (_, old) => old + text);
            if (thinking)
            {
                Push(UiMessage.ThinkingDelta(text!));
                UIMessageBus.RaiseAssistantContent("", chunks[key], key, "agent");
            }
            else
            {
                Push(UiMessage.TextDelta(text!));
                UIMessageBus.RaiseAssistantContent(chunks[key], "", key, "agent");
            }
        }

        private void ProjectTool(AgentEvent evt, bool update)
        {
            var id = evt.ToolCallId ?? Guid.NewGuid().ToString("N");
            var name = FirstNonEmpty(evt.ToolName, evt.Title, evt.ToolKind, "tool");
            var input = SerializeForUi(evt.RawInput);
            var status = evt.Status ?? "pending";
            if (update && (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)))
            {
                PushToolResult(id, string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase), SerializeForUi(evt.RawOutput));
                return;
            }

            if (_startedTools.TryAdd(id, true))
            {
                _toolStartedTicks[id] = DateTime.UtcNow.Ticks;
                Push(UiMessage.ToolCall(id, name, input, evt.Title, evt.ToolKind));
                UIMessageBus.RaiseToolCallRecorded(id, name, input);
                _onToolUse(id, name, input);
            }
        }

        private void PushToolResult(string id, bool isError, string content)
        {
            var duration = 0d;
            if (_toolStartedTicks.TryRemove(id, out var startTicks))
                duration = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - startTicks).TotalMilliseconds;
            Push(UiMessage.ToolResult(id, isError, duration, content));
            UIMessageBus.RaiseToolResultRecorded(id, isError, content);
            TokenUsageTracker.RecordToolResult(isError);
        }

        private void ProjectUsage(AgentEvent evt)
        {
            TokenUsageTracker.CurrentContextWindow = evt.ContextWindow ?? evt.SizeTokens ?? 0;
            TokenUsageTracker.CurrentInputTokens = evt.UsedTokens ?? evt.InputTokens ?? 0;
            TokenUsageTracker.CurrentCacheReadTokens = evt.CacheReadTokens ?? 0;
            TokenUsageTracker.CurrentCacheCreateTokens = evt.CacheCreateTokens ?? 0;
            Push(UiMessage.BudgetStatus(TokenUsageTracker.TotalAllTokens, AgentLoop.BudgetLimit, "Idle",
                TokenUsageTracker.TotalCacheReadTokens,
                TokenUsageTracker.TotalInputTokens + TokenUsageTracker.TotalCacheReadTokens,
                TokenUsageTracker.TotalCacheCreateTokens,
                TokenUsageTracker.CurrentContextWindow,
                TokenUsageTracker.CurrentInputTokens,
                TokenUsageTracker.CurrentCacheReadTokens,
                TokenUsageTracker.CurrentCacheCreateTokens));
        }

        private static void Push(UiMessage message) => UIMessageBus.PushUiMessage(message);

        private string SerializeForUi(JsonElement? value)
        {
            if (!value.HasValue) return "";
            try { return value.Value.GetRawText(); }
            catch (Exception ex)
            {
                _logWarn("[NodeACP] UI payload serialization failed: " + FormatExceptionChain(ex));
                return "";
            }
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
            return "tool";
        }
    }
}
