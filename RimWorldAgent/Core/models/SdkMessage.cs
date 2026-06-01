using System;
using System.Collections.Generic;
using System.Text.Json;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    // ===== 基类 =====

    /// <summary>
    /// SDK 消息基类。FromJson 工厂处理 companion bridge type=event 包装 + 类型分发。
    /// 子类字段与 @anthropic-ai/claude-agent-sdk coreSchemas.ts 对齐。
    /// 未知字段记录警告。
    /// </summary>
    public abstract class SdkMessage
    {
        public string RawJson { get; }
        public string Type { get; }

        protected SdkMessage(string rawJson, string type)
        {
            RawJson = rawJson;
            Type = type;
        }

        /// <summary>工厂：raw JSON → 类型化的 SdkMessage 子类</summary>
        public static SdkMessage FromJson(string rawJson)
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            // companion bridge 用 type=event 包装 → 解包 payload
            if (type == "event")
            {
                if (root.TryGetProperty("event", out var inner) && root.TryGetProperty("payload", out var payload))
                {
                    var innerType = inner.GetString() ?? "";
                    var knownFields = new HashSet<string> { "type", "event", "payload" };
                    ValidateFields(root, knownFields, rawJson);

                    switch (innerType)
                    {
                        case "assistant": return new SdkAssistantMessage(rawJson, payload);
                        case "stream_event": return new SdkStreamEventMessage(rawJson, payload);
                        case "result": return new SdkResultMessage(rawJson, payload);
                        case "system": return new SdkSystemMessage(rawJson, payload);
                        case "user": return new SdkUserMessage(rawJson, payload);
                        default:
                            CoreLog.Info($"[SdkMessage] 未知 inner type: {innerType}, 使用基类转发");
                            return new SdkUnknownMessage(rawJson, innerType, payload);
                    }
                }
                CoreLog.Warn($"[SdkMessage] type=event 缺少 event 或 payload 字段: {rawJson.Substring(0, Math.Min(rawJson.Length, 160))}");
                return new SdkUnknownMessage(rawJson, "event", root);
            }

            // 直接 SDK 格式（不在 companion bridge 包装中）
            switch (type)
            {
                case "assistant": return new SdkAssistantMessage(rawJson, root);
                case "stream_event": return new SdkStreamEventMessage(rawJson, root);
                case "result": return new SdkResultMessage(rawJson, root);
                case "system": return new SdkSystemMessage(rawJson, root);
                case "user": return new SdkUserMessage(rawJson, root);
                case "hello-ok": return new SdkHelloOkMessage(rawJson, root);
                case "aborted": return new SdkAbortedMessage(rawJson, root);
                default:
                    CoreLog.Info($"[SdkMessage] 未知顶层 type: {type}, 使用基类转发");
                    return new SdkUnknownMessage(rawJson, type, root);
            }
        }

        /// <summary>验证 root 中是否存在不在 knownFields 中的多余字段</summary>
        protected static void ValidateFields(JsonElement root, HashSet<string> known, string rawJson)
        {
            var extra = new List<string>();
            foreach (var prop in root.EnumerateObject())
                if (!known.Contains(prop.Name)) extra.Add(prop.Name);
            if (extra.Count > 0)
                CoreLog.Warn($"[SdkMessage] 未知字段 [{string.Join(", ", extra)}] in {rawJson}");
        }

        protected static string? Str(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
        protected static long? Long(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : (long?)null;
        protected static int? Int(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : (int?)null;
        protected static bool? Bool(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : (bool?)null;
    }

    // ===== 具体类型 =====

    /// <summary>assistant 消息（完整回复）</summary>
    public class SdkAssistantMessage : SdkMessage
    {
        public string? ParentToolUseId { get; }
        public string? Error { get; }
        public List<SdkContentBlock> Content { get; } = new();
        public SdkUsage? Usage { get; }
        public string? Model { get; }
        public string? StopReason { get; }
        public string? StopSequence { get; }

        public SdkAssistantMessage(string rawJson, JsonElement root) : base(rawJson, "assistant")
        {
            var known = new HashSet<string> { "type", "message", "parent_tool_use_id", "error", "uuid", "session_id" };
            ValidateFields(root, known, rawJson);

            ParentToolUseId = Str(root, "parent_tool_use_id");
            Error = Str(root, "error");

            if (root.TryGetProperty("message", out var msg))
            {
                var msgKnown = new HashSet<string> { "id", "type", "role", "content", "model", "stop_reason", "stop_sequence", "usage" };
                ValidateFields(msg, msgKnown, rawJson);

                Model = Str(msg, "model");
                StopReason = Str(msg, "stop_reason");
                StopSequence = Str(msg, "stop_sequence");

                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = Str(block, "type") ?? "";
                        if (bt == "text")
                            Content.Add(new SdkTextBlock(Str(block, "text") ?? ""));
                        else if (bt == "tool_use")
                            Content.Add(new SdkToolUseBlock(block));
                        else if (bt == "thinking")
                            Content.Add(new SdkThinkingBlock(Str(block, "thinking") ?? "", Str(block, "signature")));
                    }
                }

                if (msg.TryGetProperty("usage", out var usage))
                    Usage = new SdkUsage(usage);
            }
        }
    }

    /// <summary>stream_event 消息（流式增量）</summary>
    public class SdkStreamEventMessage : SdkMessage
    {
        public string? ParentToolUseId { get; }
        public int? Index { get; }
        public SdkStreamEvent? Event { get; }

        public SdkStreamEventMessage(string rawJson, JsonElement root) : base(rawJson, "stream_event")
        {
            var known = new HashSet<string> { "type", "event", "parent_tool_use_id", "index", "uuid", "session_id" };
            ValidateFields(root, known, rawJson);

            ParentToolUseId = Str(root, "parent_tool_use_id");
            Index = Int(root, "index");

            if (root.TryGetProperty("event", out var evt))
            {
                var et = Str(evt, "type") ?? "";
                if (et == "content_block_start")
                {
                    if (evt.TryGetProperty("content_block", out var cb))
                    {
                        var cbt = Str(cb, "type") ?? "";
                        if (cbt == "text")
                            Event = SdkStreamEvent.TextBlockStart(Index ?? 0);
                        else if (cbt == "thinking")
                            Event = SdkStreamEvent.ThinkingBlockStart(Index ?? 0);
                        else if (cbt == "tool_use")
                            Event = SdkStreamEvent.ToolUseBlockStart(Index ?? 0, Str(cb, "id"), Str(cb, "name"));
                        else
                            Event = new SdkStreamEvent("content_block_start", Index ?? 0, blockType: cbt);
                    }
                }
                else if (et == "content_block_delta")
                {
                    if (evt.TryGetProperty("delta", out var delta) && delta.TryGetProperty("type", out var dt))
                    {
                        var deltaType = dt.GetString() ?? "";
                        if (deltaType == "text_delta")
                            Event = SdkStreamEvent.TextDelta(Index ?? 0, Str(delta, "text") ?? "");
                        else if (deltaType == "thinking_delta")
                            Event = SdkStreamEvent.ThinkingDelta(Index ?? 0, Str(delta, "thinking") ?? "");
                        else if (deltaType == "input_json_delta")
                            Event = SdkStreamEvent.InputJsonDelta(Index ?? 0, Str(delta, "partial_json") ?? "");
                    }
                }
                else if (et == "content_block_stop")
                {
                    Event = new SdkStreamEvent("content_block_stop", Index ?? 0);
                }
                else
                {
                    Event = new SdkStreamEvent(et, Index ?? 0);
                }
            }
        }
    }

    /// <summary>result 消息（会话结束）</summary>
    public class SdkResultMessage : SdkMessage
    {
        public string Subtype { get; }  // "success" | "error_during_execution" | ...
        public string? StopReason { get; }
        public bool IsError { get; }
        public int? NumTurns { get; }
        public long? DurationMs { get; }
        public long? DurationApiMs { get; }
        public string? Result { get; }
        public double? TotalCostUsd { get; }
        public SdkUsage? Usage { get; }

        public SdkResultMessage(string rawJson, JsonElement root) : base(rawJson, "result")
        {
            var known = new HashSet<string> { "type", "subtype", "stop_reason", "is_error", "num_turns",
                "duration_ms", "duration_api_ms", "result", "total_cost_usd", "usage",
                "modelUsage", "permission_denials", "errors", "uuid", "session_id", "fast_mode_state" };
            ValidateFields(root, known, rawJson);

            Subtype = Str(root, "subtype") ?? "unknown";
            StopReason = Str(root, "stop_reason");
            IsError = Bool(root, "is_error") ?? false;
            NumTurns = Int(root, "num_turns");
            DurationMs = Long(root, "duration_ms");
            DurationApiMs = Long(root, "duration_api_ms");
            Result = Str(root, "result");
            TotalCostUsd = root.TryGetProperty("total_cost_usd", out var cost) ? cost.GetDouble() : (double?)null;

            if (root.TryGetProperty("usage", out var usage))
                Usage = new SdkUsage(usage);
        }
    }

    /// <summary>system 消息</summary>
    public class SdkSystemMessage : SdkMessage
    {
        public string Subtype { get; }  // "init" | "compact_boundary" | "status" | ...
        /// <summary>subtype=init 时可用</summary>
        public string? Model { get; }
        public string? SessionId { get; }
        public string? ClaudeCodeVersion { get; }
        public string? PermissionMode { get; }
        public string? Cwd { get; }
        public string? ApiKeySource { get; }
        public List<string> Tools { get; } = new();
        public List<string> Skills { get; } = new();
        public List<SdkMcpServerInfo> McpServers { get; } = new();

        public SdkSystemMessage(string rawJson, JsonElement root) : base(rawJson, "system")
        {
            var known = new HashSet<string> { "type", "subtype", "uuid", "session_id" };
            ValidateFields(root, known, rawJson);

            Subtype = Str(root, "subtype") ?? "";

            if (Subtype == "init")
            {
                Model = Str(root, "model");
                SessionId = Str(root, "session_id");
                ClaudeCodeVersion = Str(root, "claude_code_version");
                PermissionMode = Str(root, "permissionMode");
                Cwd = Str(root, "cwd");
                ApiKeySource = Str(root, "apiKeySource");

                if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                    foreach (var t in tools.EnumerateArray()) Tools.Add(t.GetString() ?? "");
                if (root.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
                    foreach (var s in skills.EnumerateArray()) Skills.Add(s.GetString() ?? "");
                if (root.TryGetProperty("mcp_servers", out var mcps) && mcps.ValueKind == JsonValueKind.Array)
                    foreach (var m in mcps.EnumerateArray())
                        McpServers.Add(new SdkMcpServerInfo(Str(m, "name") ?? "?", Str(m, "status") ?? "?"));
            }
        }
    }

    /// <summary>user 消息</summary>
    public class SdkUserMessage : SdkMessage
    {
        public string? ParentToolUseId { get; }
        public bool IsSynthetic { get; }
        public string? Priority { get; }
        public List<SdkContentBlock> Content { get; } = new();

        public SdkUserMessage(string rawJson, JsonElement root) : base(rawJson, "user")
        {
            var known = new HashSet<string> { "type", "message", "parent_tool_use_id", "isSynthetic",
                "timestamp", "priority", "uuid", "session_id", "tool_use_result", "isReplay" };
            ValidateFields(root, known, rawJson);

            ParentToolUseId = Str(root, "parent_tool_use_id");
            IsSynthetic = Bool(root, "isSynthetic") ?? false;
            Priority = Str(root, "priority");

            if (root.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var bt = Str(block, "type") ?? "";
                    if (bt == "text")
                        Content.Add(new SdkTextBlock(Str(block, "text") ?? ""));
                    else if (bt == "tool_result")
                        Content.Add(new SdkToolResultBlock(block));
                }
            }
        }
    }

    /// <summary>aborted 消息</summary>
    public class SdkAbortedMessage : SdkMessage
    {
        public SdkAbortedMessage(string rawJson, JsonElement root) : base(rawJson, "aborted")
        {
            var known = new HashSet<string> { "type", "uuid", "session_id" };
            ValidateFields(root, known, rawJson);
        }
    }

    /// <summary>hello-ok 消息</summary>
    public class SdkHelloOkMessage : SdkMessage
    {
        public SdkHelloOkMessage(string rawJson, JsonElement root) : base(rawJson, "hello-ok")
        {
            var known = new HashSet<string> { "type", "auth" };
            ValidateFields(root, known, rawJson);
        }
    }

    /// <summary>未知类型（透传原始 JSON 给 UI）</summary>
    public class SdkUnknownMessage : SdkMessage
    {
        public JsonElement Root { get; }

        public SdkUnknownMessage(string rawJson, string type, JsonElement root) : base(rawJson, type)
        {
            Root = root;
        }
    }

    // ===== 内容块子类型 =====

    public abstract class SdkContentBlock
    {
        public string BlockType { get; }
        protected SdkContentBlock(string blockType) { BlockType = blockType; }
    }

    public class SdkTextBlock : SdkContentBlock
    {
        public string Text { get; }
        public SdkTextBlock(string text) : base("text") { Text = text; }
    }

    public class SdkToolUseBlock : SdkContentBlock
    {
        public string Id { get; }
        public string Name { get; }
        public string Input { get; }
        public SdkToolUseBlock(JsonElement block) : base("tool_use")
        {
            Id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            Name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            Input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
        }
    }

    public class SdkThinkingBlock : SdkContentBlock
    {
        public string Thinking { get; }
        public string? Signature { get; }
        public SdkThinkingBlock(string thinking, string? signature) : base("thinking")
        { Thinking = thinking; Signature = signature; }
    }

    public class SdkToolResultBlock : SdkContentBlock
    {
        public string? ToolUseId { get; }
        public bool IsError { get; }
        public string Content { get; }
        public SdkToolResultBlock(JsonElement block) : base("tool_result")
        {
            ToolUseId = block.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() : null;
            IsError = block.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
            var c = "";
            if (block.TryGetProperty("content", out var cnt))
            {
                if (cnt.ValueKind == JsonValueKind.String) c = cnt.GetString() ?? "";
                else if (cnt.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in cnt.EnumerateArray())
                        if (item.TryGetProperty("text", out var txt)) c += txt.GetString();
                }
                else c = cnt.GetRawText();
            }
            Content = c;
        }
    }

    // ===== 流式事件 =====

    public class SdkStreamEvent
    {
        public string EventType { get; }
        public int Index { get; }
        public string? BlockType { get; }
        public string? Text { get; }
        public string? Thinking { get; }
        public string? PartialJson { get; }
        public string? ToolUseId { get; }
        public string? ToolName { get; }

        private SdkStreamEvent(string eventType, int index, string? blockType,
            string? text = null, string? thinking = null, string? partialJson = null,
            string? toolUseId = null, string? toolName = null)
        {
            EventType = eventType; Index = index; BlockType = blockType;
            Text = text; Thinking = thinking; PartialJson = partialJson;
            ToolUseId = toolUseId; ToolName = toolName;
        }

        public SdkStreamEvent(string eventType, int index, string? blockType = null)
        { EventType = eventType; Index = index; BlockType = blockType; }

        public static SdkStreamEvent TextDelta(int idx, string text)
            => new SdkStreamEvent("content_block_delta", idx, null, text: text);
        public static SdkStreamEvent ThinkingDelta(int idx, string thinking)
            => new SdkStreamEvent("content_block_delta", idx, null, thinking: thinking);
        public static SdkStreamEvent InputJsonDelta(int idx, string json)
            => new SdkStreamEvent("content_block_delta", idx, null, partialJson: json);
        public static SdkStreamEvent TextBlockStart(int idx)
            => new SdkStreamEvent("content_block_start", idx, "text");
        public static SdkStreamEvent ThinkingBlockStart(int idx)
            => new SdkStreamEvent("content_block_start", idx, "thinking");
        public static SdkStreamEvent ToolUseBlockStart(int idx, string? id, string? name)
            => new SdkStreamEvent("content_block_start", idx, "tool_use", toolUseId: id, toolName: name);
    }

    // ===== 辅助类型 =====

    public class SdkUsage
    {
        public long InputTokens { get; }
        public long OutputTokens { get; }
        public long? CacheReadInputTokens { get; }
        public long? CacheCreationInputTokens { get; }

        public SdkUsage(JsonElement usage)
        {
            InputTokens = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var n) ? n : 0;
            OutputTokens = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var n2) ? n2 : 0;
            CacheReadInputTokens = usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var n3) ? n3 : (long?)null;
            CacheCreationInputTokens = usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt64(out var n4) ? n4 : (long?)null;
        }
    }

    public class SdkMcpServerInfo
    {
        public string Name { get; }
        public string Status { get; }
        public SdkMcpServerInfo(string name, string status) { Name = name; Status = status; }
    }
}
