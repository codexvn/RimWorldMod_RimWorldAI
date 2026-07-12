using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.IPC.Generated;

namespace RimWorldAgent.Core.AgentTransport
{
    internal sealed class NodeRuntimeEventProjector
    {
        private string? _currentModelId;
        private readonly string _toolNameJsonPath;
        private readonly Action<string> _logWarn;
        private readonly Action _onActivity;
        private readonly Action<string, string?> _onResult;
        private readonly Action<string, string, string> _onToolUse;
        private readonly Action _onAborted;
        private readonly ConcurrentDictionary<string, bool> _startedTools = new ConcurrentDictionary<string, bool>();
        private readonly ConcurrentDictionary<string, long> _toolStartedTicks = new ConcurrentDictionary<string, long>();
        private readonly ConcurrentDictionary<string, string> _assistantChunks = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, string> _thoughtChunks = new ConcurrentDictionary<string, string>();
        private string? _lastStreamMessageId;
        private bool _lastStreamWasThinking;
        private bool _hasStreamBlock;
        private string _streamGeneration = Guid.NewGuid().ToString("N");
        private string? _publishedSessionId;
        private List<string> _sessionConfigSummary = new List<string>();
        private string _publishedConfigSummary = "";
        public NodeRuntimeEventProjector(string? fallbackModelId, string toolNameJsonPath, Action<string> logWarn, Action onActivity,
            Action<string, string?> onResult, Action<string, string, string> onToolUse, Action onAborted)
        {
            _currentModelId = fallbackModelId;
            _toolNameJsonPath = string.IsNullOrWhiteSpace(toolNameJsonPath) ? "$.toolCall.title" : toolNameJsonPath.Trim();
            _logWarn = logWarn;
            _onActivity = onActivity;
            _onResult = onResult;
            _onToolUse = onToolUse;
            _onAborted = onAborted;
        }

        public void SetCurrentModelId(string? modelId)
        {
            if (modelId != null && !string.IsNullOrWhiteSpace(modelId)) _currentModelId = modelId.Trim();
        }

        public void SetSessionConfigSummary(IReadOnlyList<string> summary)
        {
            _sessionConfigSummary = summary == null ? new List<string>() : new List<string>(summary);
        }

        public void PublishSessionInfo(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;
            var configSummary = string.Join(" · ", _sessionConfigSummary);
            if (string.Equals(_publishedSessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(_publishedConfigSummary, configSummary, StringComparison.Ordinal))
                return;
            _publishedSessionId = sessionId;
            _publishedConfigSummary = configSummary;
            Push(UiMessage.SessionInit(sessionId, _sessionConfigSummary));
        }

        public void Project(AgentEvent evt)
        {
            _onActivity();
            try
            {
                var sessionId = evt.SessionId;
                if (!string.IsNullOrWhiteSpace(sessionId) && !string.Equals(_publishedSessionId, sessionId, StringComparison.Ordinal))
                {
                    var safeSessionId = sessionId!;
                    AgentLoop.AgentSessionId = safeSessionId;
                    AgentLoop.RaiseSessionIdChanged(safeSessionId);
                    PublishSessionInfo(safeSessionId);
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
            ResetStreamBlock();
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
            TokenUsageTracker.Record(_currentModelId ?? "acp", input, output, cacheRead, cacheCreate, durationMs);
        }

        public void ProjectCancelled()
        {
            Push(UiMessage.Aborted());
            ResetStreamBlock();
            _onAborted();
        }

        private void ProjectText(string? messageId, string? text, ConcurrentDictionary<string, string> chunks, bool thinking)
        {
            // WebUI 依赖空 delta 表示 content block start。ACP DTO 已携带 messageId，
            // 这里将 messageId/内容类型变化转换成同样的 UiMessage 边界信号。
            var key = string.IsNullOrEmpty(messageId)
                ? (thinking ? "__acp_thinking__" : "__acp_text__") + _streamGeneration
                : messageId!;
            var explicitBlockStart = string.IsNullOrEmpty(text);
            if (explicitBlockStart
                || !_hasStreamBlock
                || !string.Equals(_lastStreamMessageId, key, StringComparison.Ordinal)
                || _lastStreamWasThinking != thinking)
            {
                if (thinking) Push(UiMessage.ThinkingDelta(""));
                else Push(UiMessage.TextDelta(""));
                _lastStreamMessageId = key;
                _lastStreamWasThinking = thinking;
                _hasStreamBlock = true;
            }

            if (string.IsNullOrEmpty(text)) return;
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

        private void ResetStreamBlock()
        {
            if (!string.IsNullOrEmpty(_lastStreamMessageId))
            {
                _assistantChunks.TryRemove(_lastStreamMessageId!, out _);
                _thoughtChunks.TryRemove(_lastStreamMessageId!, out _);
            }
            _lastStreamMessageId = null;
            _lastStreamWasThinking = false;
            _hasStreamBlock = false;
            _streamGeneration = Guid.NewGuid().ToString("N");
        }

        private void ProjectTool(AgentEvent evt, bool update)
        {
            var id = evt.ToolCallId ?? Guid.NewGuid().ToString("N");
            if (!update)
                ResetStreamBlock();
            // 工具名解析在 C#：Node 只透传 ACP 字段
            var name = ResolveToolName(evt);
            var input = SerializeForUi(evt.RawInput);
            var status = evt.Status ?? "pending";
            var content = SerializeToolContentForUi(evt.Content);
            if (update && (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)))
            {
                // 完成前若参数已补齐，仅更新 UI，不重复写入会话历史
                if (!string.IsNullOrWhiteSpace(input) && input != "{}")
                    Push(UiMessage.ToolCall(id, name, input, evt.Title, evt.ToolKind, content));
                PushToolResult(id, string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase), SerializeForUi(evt.RawOutput));
                return;
            }

            if (_startedTools.TryAdd(id, true))
            {
                _toolStartedTicks[id] = DateTime.UtcNow.Ticks;
                var permToolName = ExtractPermissionToolName(evt);
                Push(UiMessage.ToolCall(id, name, input, evt.Title, evt.ToolKind, content));
                // 会话历史只录首次 tool_call，后续参数回填只刷新 UI
                UIMessageBus.RaiseToolCallRecorded(id, name, input, permToolName);
                _onToolUse(id, name, input);
                return;
            }

            // tool_update(pending/in_progress) 回填参数/名称到 UI（Claude 常先发空 rawInput）
            if (update && ((!string.IsNullOrWhiteSpace(input) && input != "{}") ||
                           !string.IsNullOrWhiteSpace(name) ||
                           !string.IsNullOrWhiteSpace(content)))
                Push(UiMessage.ToolCall(id, name, input, evt.Title, evt.ToolKind, content));
        }

        /// <summary>
        /// 按配置的 ToolNameJsonPath 从 rawInput/title 提取权限判定用工具名。
        /// JsonPath 命中则用提取结果；未命中时回退 ACP title。
        /// </summary>
        private string ExtractPermissionToolName(AgentEvent evt)
        {
            // 优先按配置 JsonPath 提取
            if (!string.IsNullOrWhiteSpace(_toolNameJsonPath) && evt.RawInput.HasValue)
            {
                try
                {
                    var path = _toolNameJsonPath.TrimStart('$', '.');
                    if (!string.IsNullOrEmpty(path))
                    {
                        var current = evt.RawInput.Value;
                        foreach (var segment in path.Split('.'))
                        {
                            if (string.IsNullOrEmpty(segment) || current.ValueKind != JsonValueKind.Object) goto fallback;
                            if (!current.TryGetProperty(segment, out current)) goto fallback;
                        }
                        var extracted = current.ValueKind == JsonValueKind.String
                            ? current.GetString() ?? ""
                            : current.GetRawText();
                        if (!string.IsNullOrWhiteSpace(extracted)) return extracted.Trim();
                    }
                }
                catch { }
            }
            fallback:
            return evt.Title ?? "";
        }

        /// <summary>
        /// 从 ACP 透传字段解析展示/业务工具名。
        /// 优先 rawInput.action（agent gateway），其次 rawInput.tool，再次 title/toolKind。
        /// IPC 不携带 toolName；权限正则默认匹配 title/网关名，与此处 UI 展示名可能不同。
        /// </summary>
        private static string ResolveToolName(AgentEvent evt)
        {
            var action = TryGetRawInputString(evt.RawInput, "action");
            if (string.Equals(action, "execute_tool", StringComparison.OrdinalIgnoreCase) &&
                TryGetRawInputElement(evt.RawInput, "params", out var nestedParams))
            {
                var nestedTool = TryGetRawInputString(nestedParams, "tool") ??
                                  TryGetRawInputString(nestedParams, "toolName") ??
                                  TryGetRawInputString(nestedParams, "name");
                if (!string.IsNullOrWhiteSpace(nestedTool))
                {
                    var nestedServer = TryGetRawInputString(nestedParams, "server");
                    return string.IsNullOrWhiteSpace(nestedServer)
                        ? nestedTool!
                        : $"mcp.{nestedServer}.{nestedTool}";
                }
            }

            if (!string.IsNullOrWhiteSpace(action)) return action!;

            var tool = TryGetRawInputString(evt.RawInput, "tool");
            if (!string.IsNullOrWhiteSpace(tool))
            {
                var server = TryGetRawInputString(evt.RawInput, "server");
                return string.IsNullOrWhiteSpace(server) ? tool! : ("mcp." + server + "." + tool);
            }

            return FirstNonEmpty(evt.Title, evt.ToolKind, "tool");
        }

        private static string? TryGetRawInputString(JsonElement? rawInput, string property)
        {
            if (!TryGetRawInputElement(rawInput, property, out var el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }

        private static bool TryGetRawInputElement(JsonElement? rawInput, string property, out JsonElement value)
        {
            value = default;
            return rawInput.HasValue && rawInput.Value.ValueKind == JsonValueKind.Object &&
                   rawInput.Value.TryGetProperty(property, out value);
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
            if (evt.UsedTokens.HasValue) TokenUsageTracker.CurrentContextUsedTokens = evt.UsedTokens.Value;
            if (evt.InputTokens.HasValue) TokenUsageTracker.CurrentInputTokens = evt.InputTokens.Value;
            if (evt.CacheReadTokens.HasValue) TokenUsageTracker.CurrentCacheReadTokens = evt.CacheReadTokens.Value;
            if (evt.CacheCreateTokens.HasValue) TokenUsageTracker.CurrentCacheCreateTokens = evt.CacheCreateTokens.Value;
            Push(UiMessage.BudgetStatus(TokenUsageTracker.TotalAllTokens, AgentLoop.BudgetLimit, "Idle",
                TokenUsageTracker.TotalCacheReadTokens,
                TokenUsageTracker.TotalInputTokens + TokenUsageTracker.TotalCacheReadTokens,
                TokenUsageTracker.TotalCacheCreateTokens,
                TokenUsageTracker.CurrentContextWindow,
                TokenUsageTracker.CurrentInputTokens,
                TokenUsageTracker.CurrentCacheReadTokens,
                TokenUsageTracker.CurrentCacheCreateTokens,
                TokenUsageTracker.CurrentContextUsedTokens));
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

        private string SerializeToolContentForUi(JsonElement? value)
        {
            if (!value.HasValue || value.Value.ValueKind == JsonValueKind.Null) return "";
            try
            {
                if (value.Value.ValueKind != JsonValueKind.Array)
                    return ExtractToolContentText(value.Value);

                var parts = new List<string>();
                foreach (var item in value.Value.EnumerateArray())
                {
                    var text = ExtractToolContentText(item);
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                }
                return string.Join("\n", parts);
            }
            catch (Exception ex)
            {
                _logWarn("[NodeACP] tool content serialization failed: " + FormatExceptionChain(ex));
                return SerializeForUi(value);
            }
        }

        private static string ExtractToolContentText(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
            if (value.ValueKind != JsonValueKind.Object) return value.GetRawText();

            if (value.TryGetProperty("content", out var nested))
                return ExtractToolContentText(nested);
            if (value.TryGetProperty("text", out var text))
                return text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : text.GetRawText();
            return value.GetRawText();
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
