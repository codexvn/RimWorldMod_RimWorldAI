using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.CcbManager
{
    // ===== 序列化配置 =====

    /// <summary>反序列化器共享配置</summary>
    internal static class SdkMessageSerializer
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new ContentBlockConverter(),
                new StreamEventConverter(),
            },
        };

        /// <summary>检查 [JsonExtensionData] 收集的未知字段并记录警告</summary>
        public static void WarnExtra(SdkMessage msg, string rawJson)
        {
            if (msg.ExtraFields == null || msg.ExtraFields.Count == 0) return;
            var names = string.Join(", ", msg.ExtraFields.Keys);
            CoreLog.Warn($"[SdkMessage] 未知字段 [{names}] in {rawJson}");
        }

        /// <summary>检查 DTO 中的未知字段</summary>
        public static void WarnDtoExtra(Dictionary<string, JsonElement>? extra, string context, string rawJson)
        {
            if (extra == null || extra.Count == 0) return;
            var names = string.Join(", ", extra.Keys);
            CoreLog.Warn($"[SdkMessage] 未知字段 [{names}] in {context}: {rawJson}");
        }
    }

    // ===== 基类 =====

    /// <summary>
    /// SDK 消息基类。FromJson 工厂处理 companion bridge type=event 包装 + 类型分发。
    /// 子类字段通过 [JsonPropertyName] 映射，与 @anthropic-ai/claude-agent-sdk coreSchemas.ts 对齐。
    /// 未知字段通过 [JsonExtensionData] 收集并记录警告。
    /// </summary>
    public abstract class SdkMessage
    {
        /// <summary>原始 JSON 字符串</summary>
        [JsonIgnore] public string RawJson { get; internal set; } = "";
        /// <summary>消息类型标识符（assistant / stream_event / result / system / user / hello-ok / aborted）</summary>
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        /// <summary>消息唯一标识符（UUID）</summary>
        [JsonPropertyName("uuid")] public string? Uuid { get; set; }
        /// <summary>会话 ID</summary>
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        /// <summary>未知字段收集器（[JsonExtensionData] 自动填充）</summary>
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtraFields { get; set; }

        /// <summary>工厂：raw JSON → 类型化的 SdkMessage 子类</summary>
        public static SdkMessage FromJson(string rawJson)
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            // companion bridge 用 type=event 包装 → 解包 payload
            if (type == "event")
            {
                if (!root.TryGetProperty("event", out var inner) || !root.TryGetProperty("payload", out var payload))
                {
                    CoreLog.Warn($"[SdkMessage] type=event 缺少 event 或 payload 字段: {rawJson.Substring(0, Math.Min(rawJson.Length, 160))}");
                    return new SdkUnknownMessage(rawJson, "event");
                }
                type = inner.GetString() ?? "";
                string bodyJson = payload.GetRawText();

                // 外层包装字段校验
                var knownOuter = new HashSet<string> { "type", "event", "payload" };
                foreach (var prop in root.EnumerateObject())
                    if (!knownOuter.Contains(prop.Name))
                        CoreLog.Warn($"[SdkMessage] 未知字段 [{prop.Name}] in wrapper: {rawJson}");
                return Build(type, bodyJson, rawJson);
            }
            return Build(type, rawJson, rawJson);
        }

        private static SdkMessage Build(string type, string bodyJson, string rawJson)
        {
            SdkMessage? msg = type switch
            {
                "assistant" => JsonSerializer.Deserialize<SdkAssistantMessage>(bodyJson, SdkMessageSerializer.Options),
                "stream_event" => JsonSerializer.Deserialize<SdkStreamEventMessage>(bodyJson, SdkMessageSerializer.Options),
                "result" => JsonSerializer.Deserialize<SdkResultMessage>(bodyJson, SdkMessageSerializer.Options),
                "system" => SdkSystemMessage.FromJson(bodyJson, rawJson),
                "user" => JsonSerializer.Deserialize<SdkUserMessage>(bodyJson, SdkMessageSerializer.Options),
                "hello-ok" => JsonSerializer.Deserialize<SdkHelloOkMessage>(bodyJson, SdkMessageSerializer.Options),
                "aborted" => JsonSerializer.Deserialize<SdkAbortedMessage>(bodyJson, SdkMessageSerializer.Options),
                _ => null,
            };

            if (msg == null)
            {
                CoreLog.Info($"[SdkMessage] 未知顶层 type: {type}, 使用基类转发");
                return new SdkUnknownMessage(rawJson, type);
            }

            msg.RawJson = rawJson;
            SdkMessageSerializer.WarnExtra(msg, rawJson);
            return msg;
        }
    }

    // ===== 具体类型 =====

    /// <summary>
    /// assistant 消息 — AI 完整回复。
    /// 包含 content 块（text / tool_use / thinking）、usage、model、stop_reason。
    /// </summary>
    public class SdkAssistantMessage : SdkMessage
    {
        [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }

        [JsonPropertyName("message")] public AssistMsgBody? AssistMsg { get; set; }

        /// <summary>消息 ID（message.id）</summary>
        public string? MessageId => AssistMsg?.Id;
        /// <summary>内容块列表（文本 / 工具调用 / 思考）</summary>
        public List<SdkContentBlock> Content => AssistMsg?.Content ?? new();
        /// <summary>Token 使用统计</summary>
        public SdkUsage? Usage => AssistMsg?.Usage;
        /// <summary>模型标识符（如 "claude-sonnet-4-6"）</summary>
        public string? Model => AssistMsg?.Model;
        /// <summary>停止原因（end_turn / max_tokens / stop_sequence / tool_use）</summary>
        public string? StopReason => AssistMsg?.StopReason;
        /// <summary>触发停止的序列文本，仅 stop_sequence 时有值</summary>
        public string? StopSequence => AssistMsg?.StopSequence;
    }

    public class AssistMsgBody
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? MsgType { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
        [JsonPropertyName("stop_sequence")] public string? StopSequence { get; set; }
        [JsonPropertyName("content")] public List<SdkContentBlock> Content { get; set; } = new();
        [JsonPropertyName("usage")] public SdkUsage? Usage { get; set; }
        [JsonPropertyName("service_tier")] public string? ServiceTier { get; set; }
        [JsonPropertyName("context_management")] public JsonElement? CtxMgmt { get; set; }
    }

    /// <summary>
    /// stream_event 消息 — 流式增量事件。
    /// 逐块推送 AI 回复内容（content_block_start → content_block_delta… → content_block_stop）。
    /// </summary>
    public class SdkStreamEventMessage : SdkMessage
    {
        [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; set; }
        /// <summary>首 Token 延迟（ms），SDK 发出请求到首个 token 返回的时间</summary>
        [JsonPropertyName("ttft_ms")] public long? TtftMs { get; set; }
        /// <summary>流式事件详情</summary>
        [JsonPropertyName("event")] [JsonConverter(typeof(StreamEventConverter))] public SdkStreamEvent? Event { get; set; }
        /// <summary>内容块在回复中的索引位置</summary>
        public int? Index => Event?.Index;
    }

    /// <summary>
    /// result 消息 — 会话结束。
    /// 包含成功/失败状态、耗时、token 统计、费用等信息。
    /// </summary>
    public class SdkResultMessage : SdkMessage
    {
        [JsonPropertyName("subtype")] public string Subtype { get; set; } = "";
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
        [JsonPropertyName("is_error")] public bool IsError { get; set; }
        [JsonPropertyName("num_turns")] public int? NumTurns { get; set; }
        [JsonPropertyName("duration_ms")] public long? DurationMs { get; set; }
        [JsonPropertyName("duration_api_ms")] public long? DurationApiMs { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
        [JsonPropertyName("total_cost_usd")] public double? TotalCostUsd { get; set; }
        [JsonPropertyName("usage")] public SdkUsage? Usage { get; set; }
        [JsonPropertyName("modelUsage")] public Dictionary<string, SdkModelUsage> ModelUsage { get; set; } = new();
        [JsonPropertyName("permission_denials")] public List<SdkPermissionDenial> PermissionDenials { get; set; } = new();
        [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
        [JsonPropertyName("fast_mode_state")] public string? FastModeState { get; set; }
        [JsonPropertyName("structured_output")] public string? StructuredOutput { get; set; }
        [JsonPropertyName("api_error_status")] public int? ApiErrorStatus { get; set; }
        [JsonPropertyName("ttft_ms")] public long? TtftMs { get; set; }
        [JsonPropertyName("terminal_reason")] public string? TerminalReason { get; set; }
    }

    /// <summary>
    /// system 消息 — 系统生命周期事件（抽象基类）。
    /// 子类型按 subtype 分发到具体子类（SdkSystemInitMessage 等）。
    /// </summary>
    public abstract partial class SdkSystemMessage : SdkMessage
    {
        [JsonPropertyName("subtype")] public string Subtype { get; set; } = "";
    }

    /// <summary>
    /// user 消息 — SDK 回显的用户消息。
    /// 包含用户发送的 text + tool_result 内容块，以及元数据。
    /// </summary>
    public class SdkUserMessage : SdkMessage
    {
        [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; set; }
        [JsonPropertyName("isSynthetic")] public bool IsSynthetic { get; set; }
        [JsonPropertyName("priority")] public string? Priority { get; set; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("isReplay")] public bool? IsReplay { get; set; }
        [JsonPropertyName("tool_use_result")] public JsonElement? ToolUseResultRaw { get; set; }
        [JsonPropertyName("message")] public UserMsgBody? UserMsg { get; set; }
        /// <summary>内容块列表（text / tool_result）</summary>
        public List<SdkContentBlock> Content => UserMsg?.Content ?? new();
    }

    public class UserMsgBody
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public List<SdkContentBlock> Content { get; set; } = new();
    }

    /// <summary>
    /// aborted 消息 — 中断确认。
    /// SDK 确认已收到并处理了中断请求。
    /// </summary>
    public class SdkAbortedMessage : SdkMessage { }

    /// <summary>
    /// hello-ok 消息 — WebSocket 握手确认。
    /// companion bridge 在收到 hello 后回复此消息，表示连接已就绪。
    /// </summary>
    public class SdkHelloOkMessage : SdkMessage
    {
        [JsonPropertyName("auth")] public string? Auth { get; set; }
    }

    /// <summary>
    /// 未知类型消息 — 当 JSON type 字段无法匹配任何已知子类时的兜底。
    /// 存储原始 JSON 供上层自行处理。
    /// </summary>
    public class SdkUnknownMessage : SdkMessage
    {
        public string RawBody { get; }
        public SdkUnknownMessage(string rawBody, string type)
        {
            RawBody = rawBody;
            Type = type;
            RawJson = rawBody;
        }
    }

    // ===== 内容块子类型 =====

    /// <summary>内容块抽象基类。子类对应 text / tool_use / thinking / tool_result 四种块类型。</summary>
    public abstract class SdkContentBlock
    {
        /// <summary>块类型标识：text / tool_use / thinking / tool_result</summary>
        [JsonPropertyName("type")] public string BlockType { get; set; } = "";
    }

    /// <summary>文本内容块 — AI 回复的正文文本。</summary>
    public class SdkTextBlock : SdkContentBlock
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        public SdkTextBlock() { BlockType = "text"; }
        public SdkTextBlock(string text) { BlockType = "text"; Text = text; }
    }

    /// <summary>工具调用内容块 — AI 请求调用某个工具。</summary>
    public class SdkToolUseBlock : SdkContentBlock
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>工具调用参数（JSON 字符串，SDK 以对象形式发送，由 RawJsonConverter 转换）</summary>
        [JsonPropertyName("input")]
        [JsonConverter(typeof(RawJsonConverter))]
        public string Input { get; set; } = "{}";
        public SdkToolUseBlock() { BlockType = "tool_use"; }
    }

    /// <summary>思考内容块 — AI 的推理过程（extended thinking）。</summary>
    public class SdkThinkingBlock : SdkContentBlock
    {
        [JsonPropertyName("thinking")] public string Thinking { get; set; } = "";
        [JsonPropertyName("signature")] public string? Signature { get; set; }
        public SdkThinkingBlock() { BlockType = "thinking"; }
    }

    /// <summary>工具结果内容块 — 工具执行结果的回显。</summary>
    public class SdkToolResultBlock : SdkContentBlock
    {
        [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; set; }
        [JsonPropertyName("is_error")] public bool IsError { get; set; }
        /// <summary>工具执行返回的内容（字符串 / 数组 / 对象，由 RawJsonConverter 自动展平为文本）</summary>
        [JsonPropertyName("content")]
        [JsonConverter(typeof(RawJsonConverter))]
        public string Content { get; set; } = "";
        public SdkToolResultBlock() { BlockType = "tool_result"; }
    }

    // ===== 自定义转换器 =====

    /// <summary>JSON 任意值 → 字符串。对象/数组返回原始 JSON 文本，字符串返回内容。</summary>
    internal class RawJsonConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Null:
                    reader.GetString(); // consume
                    return "";
                default:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                        return doc.RootElement.GetRawText();
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    // ===== ContentBlock 多态反序列化器 =====

    internal class ContentBlockConverter : JsonConverter<SdkContentBlock>
    {
        public override SdkContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var bt = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            return bt switch
            {
                "text" => JsonSerializer.Deserialize<SdkTextBlock>(root.GetRawText(), options),
                "tool_use" => JsonSerializer.Deserialize<SdkToolUseBlock>(root.GetRawText(), options),
                "thinking" => JsonSerializer.Deserialize<SdkThinkingBlock>(root.GetRawText(), options),
                "tool_result" => JsonSerializer.Deserialize<SdkToolResultBlock>(root.GetRawText(), options),
                _ => new SdkTextBlock("(unknown content type)")
            };
        }

        public override void Write(Utf8JsonWriter writer, SdkContentBlock value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    // ===== 流式事件 =====

    /// <summary>
    /// SDK 流式事件 — stream_event 消息的 event 字段。
    /// 表示流式回复中的增量变化（块开始 / 增量文本 / 块结束）。
    /// </summary>
    public class SdkStreamEvent
    {
        /// <summary>事件类型：content_block_start / content_block_delta / content_block_stop</summary>
        public string EventType { get; set; } = "";
        /// <summary>内容块在回复中的索引位置</summary>
        public int Index { get; set; }
        /// <summary>块的子类型：text / thinking / tool_use（仅 content_block_start 时有值）</summary>
        public string? BlockType { get; set; }
        /// <summary>文本增量内容（text_delta）</summary>
        public string? Text { get; set; }
        /// <summary>思考增量内容（thinking_delta）</summary>
        public string? Thinking { get; set; }
        /// <summary>工具参数 JSON 增量（input_json_delta）</summary>
        public string? PartialJson { get; set; }
        /// <summary>工具调用 ID（tool_use block_start 时赋值）</summary>
        public string? ToolUseId { get; set; }
        /// <summary>工具名称（tool_use block_start 时赋值）</summary>
        public string? ToolName { get; set; }

        public SdkStreamEvent() { }
        public SdkStreamEvent(string eventType, int index, string? blockType = null)
        { EventType = eventType; Index = index; BlockType = blockType; }

        /// <summary>创建文本增量事件</summary>
        public static SdkStreamEvent TextDelta(int idx, string text)
            => new("content_block_delta", idx) { Text = text };
        /// <summary>创建思考增量事件</summary>
        public static SdkStreamEvent ThinkingDelta(int idx, string thinking)
            => new("content_block_delta", idx) { Thinking = thinking };
        /// <summary>创建工具参数 JSON 增量事件</summary>
        public static SdkStreamEvent InputJsonDelta(int idx, string json)
            => new("content_block_delta", idx) { PartialJson = json };
        /// <summary>创建文本块开始事件</summary>
        public static SdkStreamEvent TextBlockStart(int idx)
            => new("content_block_start", idx, "text");
        /// <summary>创建思考块开始事件</summary>
        public static SdkStreamEvent ThinkingBlockStart(int idx)
            => new("content_block_start", idx, "thinking");
        /// <summary>创建工具调用块开始事件</summary>
        public static SdkStreamEvent ToolUseBlockStart(int idx, string? id, string? name)
            => new("content_block_start", idx, "tool_use") { ToolUseId = id, ToolName = name };
    }

    /// <summary>SdkStreamEvent 多态反序列化器</summary>
    internal class StreamEventConverter : JsonConverter<SdkStreamEvent>
    {
        public override SdkStreamEvent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            var et = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            var idx = root.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : 0;
            return et switch
            {
                "content_block_start" => ParseBlockStart(root, idx),
                "content_block_delta" => ParseBlockDelta(root, idx),
                "content_block_stop" => new SdkStreamEvent("content_block_stop", idx),
                _ => new SdkStreamEvent(et, idx)
            };
        }

        private static SdkStreamEvent ParseBlockStart(JsonElement root, int idx)
        {
            if (!root.TryGetProperty("content_block", out var cb))
                return new SdkStreamEvent("content_block_start", idx);
            var cbt = cb.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            return cbt switch
            {
                "text" => SdkStreamEvent.TextBlockStart(idx),
                "thinking" => SdkStreamEvent.ThinkingBlockStart(idx),
                "tool_use" => SdkStreamEvent.ToolUseBlockStart(idx,
                    cb.TryGetProperty("id", out var tui) ? tui.GetString() : null,
                    cb.TryGetProperty("name", out var tun) ? tun.GetString() : null),
                _ => new SdkStreamEvent("content_block_start", idx, cbt)
            };
        }

        private static SdkStreamEvent ParseBlockDelta(JsonElement root, int idx)
        {
            if (!root.TryGetProperty("delta", out var delta))
                return new SdkStreamEvent("content_block_delta", idx);
            var dt = delta.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
            return dt switch
            {
                "text_delta" => SdkStreamEvent.TextDelta(idx, delta.TryGetProperty("text", out var tt) ? tt.GetString() ?? "" : ""),
                "thinking_delta" => SdkStreamEvent.ThinkingDelta(idx, delta.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : ""),
                "input_json_delta" => SdkStreamEvent.InputJsonDelta(idx, delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : ""),
                _ => new SdkStreamEvent("content_block_delta", idx)
            };
        }

        public override void Write(Utf8JsonWriter writer, SdkStreamEvent value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, options);
    }

    // ===== 辅助类型 =====

    /// <summary>
    /// Token 使用统计（对齐 @anthropic-ai/sdk Usage 接口）。
    /// 仅包含 Anthropic API 原始响应的 8 个字段。
    /// SDK enrich 字段（contextWindow/costUSD/maxOutputTokens）见 SdkModelUsage。
    /// </summary>
    public class SdkUsage
    {
        [JsonPropertyName("input_tokens")] public long InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public long OutputTokens { get; set; }
        [JsonPropertyName("cache_read_input_tokens")] public long? CacheReadInputTokens { get; set; }
        [JsonPropertyName("cache_creation_input_tokens")] public long? CacheCreationInputTokens { get; set; }
        [JsonPropertyName("cache_creation")] public SdkCacheCreation? CacheCreation { get; set; }
        [JsonPropertyName("server_tool_use")] public SdkServerToolUsage? ServerToolUse { get; set; }
        [JsonPropertyName("service_tier")] public string? ServiceTier { get; set; }
        [JsonPropertyName("inference_geo")] public string? InferenceGeo { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    }

    /// <summary>
    /// Cache TTL 分布（@anthropic-ai/sdk CacheCreation）。
    /// Anthropic API 有两级 prompt cache：5 分钟和 1 小时。
    /// </summary>
    public class SdkCacheCreation
    {
        [JsonPropertyName("ephemeral_1h_input_tokens")] public long Ephemeral1hInputTokens { get; set; }
        [JsonPropertyName("ephemeral_5m_input_tokens")] public long Ephemeral5mInputTokens { get; set; }
    }

    /// <summary>
    /// 服务端工具调用统计（@anthropic-ai/sdk ServerToolUsage）。
    /// </summary>
    public class SdkServerToolUsage
    {
        [JsonPropertyName("web_fetch_requests")] public long WebFetchRequests { get; set; }
        [JsonPropertyName("web_search_requests")] public long WebSearchRequests { get; set; }
    }

    /// <summary>
    /// 单模型 Token 使用详情（modelUsage 字典的子项）。
    /// 对齐 coreSchemas.ts ModelUsageSchema。
    /// </summary>
    public class SdkModelUsage
    {
        [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
        [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
        [JsonPropertyName("cacheReadInputTokens")] public long CacheReadInputTokens { get; set; }
        [JsonPropertyName("cacheCreationInputTokens")] public long CacheCreationInputTokens { get; set; }
        [JsonPropertyName("webSearchRequests")] public long WebSearchRequests { get; set; }
        [JsonPropertyName("costUSD")] public double CostUsd { get; set; }
        [JsonPropertyName("contextWindow")] public long ContextWindow { get; set; }
        [JsonPropertyName("maxOutputTokens")] public long MaxOutputTokens { get; set; }
    }

    /// <summary>
    /// 被权限系统拒绝的工具调用记录。
    /// </summary>
    public class SdkPermissionDenial
    {
        [JsonPropertyName("tool_name")] public string ToolName { get; set; } = "";
        [JsonPropertyName("tool_use_id")] public string ToolUseId { get; set; } = "";
        [JsonPropertyName("tool_input")] public string ToolInput { get; set; } = "{}";
    }

    /// <summary>
    /// 已加载的插件信息。
    /// </summary>
    public class SdkPluginInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    /// <summary>
    /// MCP 服务器连接信息。
    /// 包含服务器名称和当前连接状态（connected / failed / needs-auth / pending / disabled）。
    /// </summary>
    public class SdkMcpServerInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
    }

    /// <summary>
    /// 上下文压缩元数据（subtype=compact_boundary 时携带）。
    /// </summary>
    public class SdkCompactMetadata
    {
        [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";
        [JsonPropertyName("pre_tokens")] public long PreTokens { get; set; }
        [JsonPropertyName("preserved_segment")] public SdkPreservedSegment? PreservedSegment { get; set; }
        public string? PreservedHeadUuid => PreservedSegment?.HeadUuid;
        public string? PreservedAnchorUuid => PreservedSegment?.AnchorUuid;
        public string? PreservedTailUuid => PreservedSegment?.TailUuid;
    }

    /// <summary>preserved_segment 子对象</summary>
    public class SdkPreservedSegment
    {
        [JsonPropertyName("head_uuid")] public string? HeadUuid { get; set; }
        [JsonPropertyName("anchor_uuid")] public string? AnchorUuid { get; set; }
        [JsonPropertyName("tail_uuid")] public string? TailUuid { get; set; }
    }

    /// <summary>
    /// 记忆文件路径配置（subtype=init 运行时字段）。
    /// 例如 {"auto":"C:\\Users\\...\\memory\\"}
    /// </summary>
    public class SdkMemoryPaths
    {
        [JsonPropertyName("auto")] public string? Auto { get; set; }
    }
}
