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
    /// 未知字段记录警告，不拒绝消息。
    /// </summary>
    public abstract class SdkMessage
    {
        /// <summary>原始 JSON 字符串</summary>
        public string RawJson { get; }
        /// <summary>消息类型标识符（assistant / stream_event / result / system / user / hello-ok / aborted）</summary>
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

        /// <summary>验证 root 中是否存在不在 knownFields 中的多余字段。存在时记录警告但继续处理。</summary>
        protected static void ValidateFields(JsonElement root, HashSet<string> known, string rawJson)
        {
            var extra = new List<string>();
            foreach (var prop in root.EnumerateObject())
                if (!known.Contains(prop.Name)) extra.Add(prop.Name);
            if (extra.Count > 0)
                CoreLog.Warn($"[SdkMessage] 未知字段 [{string.Join(", ", extra)}] in {rawJson}");
        }

        /// <summary>安全读取 string 字段，不存在或 null 返回 null</summary>
        protected static string? Str(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
        /// <summary>安全读取 long 字段，不存在或非数字返回 null</summary>
        protected static long? Long(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : (long?)null;
        /// <summary>安全读取 int 字段，不存在或非数字返回 null</summary>
        protected static int? Int(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : (int?)null;
        /// <summary>安全读取 bool 字段，不存在或 null 返回 null</summary>
        protected static bool? Bool(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetBoolean() : (bool?)null;
    }

    // ===== 具体类型 =====

    /// <summary>
    /// assistant 消息 — AI 完整回复。
    /// 包含 content 块（text / tool_use / thinking）、usage、model、stop_reason。
    /// </summary>
    public class SdkAssistantMessage : SdkMessage
    {
        /// <summary>工具调用链中的父工具调用 ID，顶层为 null</summary>
        public string? ParentToolUseId { get; }
        /// <summary>API 调用错误类型（authentication_failed / rate_limit / server_error 等），仅出错时有值</summary>
        public string? Error { get; }
        /// <summary>内容块列表（文本 / 工具调用 / 思考）</summary>
        public List<SdkContentBlock> Content { get; } = new();
        /// <summary>Token 使用统计</summary>
        public SdkUsage? Usage { get; }
        /// <summary>模型标识符（如 "claude-sonnet-4-6"）</summary>
        public string? Model { get; }
        /// <summary>停止原因（end_turn / max_tokens / stop_sequence / tool_use）</summary>
        public string? StopReason { get; }
        /// <summary>触发停止的序列文本，仅 stop_sequence 时有值</summary>
        public string? StopSequence { get; }

        public SdkAssistantMessage(string rawJson, JsonElement root) : base(rawJson, "assistant")
        {
            var known = new HashSet<string> { "type", "message", "parent_tool_use_id", "error", "uuid", "session_id", "context_management" };
            ValidateFields(root, known, rawJson);

            ParentToolUseId = Str(root, "parent_tool_use_id");
            Error = Str(root, "error");

            if (root.TryGetProperty("message", out var msg))
            {
                var msgKnown = new HashSet<string> { "id", "type", "role", "content", "model", "stop_reason", "stop_sequence", "usage", "context_management" };
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

    /// <summary>
    /// stream_event 消息 — 流式增量事件。
    /// 逐块推送 AI 回复内容（content_block_start → content_block_delta… → content_block_stop）。
    /// </summary>
    public class SdkStreamEventMessage : SdkMessage
    {
        /// <summary>工具调用链中的父工具调用 ID</summary>
        public string? ParentToolUseId { get; }
        /// <summary>内容块在回复中的索引位置</summary>
        public int? Index { get; }
        /// <summary>流式事件详情</summary>
        public SdkStreamEvent? Event { get; }

        public SdkStreamEventMessage(string rawJson, JsonElement root) : base(rawJson, "stream_event")
        {
            var known = new HashSet<string> { "type", "event", "parent_tool_use_id", "index", "uuid", "session_id", "ttft_ms" };
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

    /// <summary>
    /// result 消息 — 会话结束。
    /// 包含成功/失败状态、耗时、token 统计等信息。
    /// </summary>
    public class SdkResultMessage : SdkMessage
    {
        /// <summary>结束类型：success / error_during_execution / error_max_turns / error_max_budget_usd / error_max_structured_output_retries</summary>
        public string Subtype { get; }
        /// <summary>停止原因，null 表示正常完成</summary>
        public string? StopReason { get; }
        /// <summary>是否为错误结束</summary>
        public bool IsError { get; }
        /// <summary>会话总轮次（API 往返次数）</summary>
        public int? NumTurns { get; }
        /// <summary>会话总耗时（ms，含网络和工具执行）</summary>
        public long? DurationMs { get; }
        /// <summary>API 调用总耗时（ms）</summary>
        public long? DurationApiMs { get; }
        /// <summary>AI 最终回复文本</summary>
        public string? Result { get; }
        /// <summary>总费用（美元）</summary>
        public double? TotalCostUsd { get; }
        /// <summary>Token 使用统计</summary>
        public SdkUsage? Usage { get; }

        public SdkResultMessage(string rawJson, JsonElement root) : base(rawJson, "result")
        {
            var known = new HashSet<string> { "type", "subtype", "stop_reason", "is_error", "num_turns",
                "duration_ms", "duration_api_ms", "result", "total_cost_usd", "usage",
                "modelUsage", "permission_denials", "errors", "uuid", "session_id", "fast_mode_state", "structured_output" };
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

    /// <summary>
    /// system 消息 — 系统生命周期事件。
    /// 子类型包括 init（初始化配置）、status（运行状态变更）、compact_boundary（上下文压缩分界）等。
    /// </summary>
    public class SdkSystemMessage : SdkMessage
    {
        /// <summary>子类型：init / status / compact_boundary / post_turn_summary / api_retry / task_notification / task_started / task_progress / session_state_changed / hook_started / hook_progress / hook_response / files_persisted / elicitation_complete / local_command_output</summary>
        public string Subtype { get; }
        /// <summary>当前使用的模型标识符（subtype=init）</summary>
        public string? Model { get; }
        /// <summary>会话 ID（subtype=init）</summary>
        public string? SessionId { get; }
        /// <summary>Claude Code 版本号（subtype=init）</summary>
        public string? ClaudeCodeVersion { get; }
        /// <summary>权限模式：default / acceptEdits / bypassPermissions / plan / dontAsk（subtype=init/status）</summary>
        public string? PermissionMode { get; }
        /// <summary>当前工作目录绝对路径（subtype=init）</summary>
        public string? Cwd { get; }
        /// <summary>API Key 来源：user / project / org / temporary / oauth（subtype=init）</summary>
        public string? ApiKeySource { get; }
        /// <summary>可用工具名称列表（subtype=init）</summary>
        public List<string> Tools { get; } = new();
        /// <summary>已注册 Skill 名称列表（subtype=init）</summary>
        public List<string> Skills { get; } = new();
        /// <summary>MCP 服务器连接状态列表（subtype=init）</summary>
        public List<SdkMcpServerInfo> McpServers { get; } = new();

        public SdkSystemMessage(string rawJson, JsonElement root) : base(rawJson, "system")
        {
            var known = new HashSet<string> { "type", "subtype", "uuid", "session_id", "model", "claude_code_version", "permissionMode", "cwd", "apiKeySource", "tools", "skills", "mcp_servers", "slash_commands", "output_style", "agents", "plugins", "betas", "fast_mode_state", "status", "compact_metadata", "analytics_disabled", "product_feedback_disabled", "memory_paths" };
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

    /// <summary>
    /// user 消息 — SDK 回显的用户消息。
    /// 包含用户发送的 text + tool_result 内容块，以及元数据。
    /// </summary>
    public class SdkUserMessage : SdkMessage
    {
        /// <summary>工具调用链中的父工具调用 ID</summary>
        public string? ParentToolUseId { get; }
        /// <summary>是否为 SDK 内部合成的消息（非真实用户输入）</summary>
        public bool IsSynthetic { get; }
        /// <summary>消息优先级：now / next / later</summary>
        public string? Priority { get; }
        /// <summary>内容块列表（text / tool_result）</summary>
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

    /// <summary>
    /// aborted 消息 — 中断确认。
    /// SDK 确认已收到并处理了中断请求。
    /// </summary>
    public class SdkAbortedMessage : SdkMessage
    {
        public SdkAbortedMessage(string rawJson, JsonElement root) : base(rawJson, "aborted")
        {
            var known = new HashSet<string> { "type", "uuid", "session_id" };
            ValidateFields(root, known, rawJson);
        }
    }

    /// <summary>
    /// hello-ok 消息 — WebSocket 握手确认。
    /// companion bridge 在收到 hello 后回复此消息，表示连接已就绪。
    /// </summary>
    public class SdkHelloOkMessage : SdkMessage
    {
        public SdkHelloOkMessage(string rawJson, JsonElement root) : base(rawJson, "hello-ok")
        {
            var known = new HashSet<string> { "type", "auth" };
            ValidateFields(root, known, rawJson);
        }
    }

    /// <summary>
    /// 未知类型消息 — 当 JSON type 字段无法匹配任何已知子类时的兜底。
    /// 携带完整的 Root JsonElement 供上层自行处理。
    /// </summary>
    public class SdkUnknownMessage : SdkMessage
    {
        /// <summary>完整的 JSON 根元素</summary>
        public JsonElement Root { get; }

        public SdkUnknownMessage(string rawJson, string type, JsonElement root) : base(rawJson, type)
        {
            Root = root;
        }
    }

    // ===== 内容块子类型 =====

    /// <summary>内容块抽象基类。子类对应 text / tool_use / thinking / tool_result 四种块类型。</summary>
    public abstract class SdkContentBlock
    {
        /// <summary>块类型标识：text / tool_use / thinking / tool_result</summary>
        public string BlockType { get; }
        protected SdkContentBlock(string blockType) { BlockType = blockType; }
    }

    /// <summary>文本内容块 — AI 回复的正文文本。</summary>
    public class SdkTextBlock : SdkContentBlock
    {
        /// <summary>文本内容</summary>
        public string Text { get; }
        public SdkTextBlock(string text) : base("text") { Text = text; }
    }

    /// <summary>工具调用内容块 — AI 请求调用某个工具。</summary>
    public class SdkToolUseBlock : SdkContentBlock
    {
        /// <summary>工具调用唯一标识符，关联 tool_result</summary>
        public string Id { get; }
        /// <summary>工具名称（如 "mcp__agent__get_game_context"）</summary>
        public string Name { get; }
        /// <summary>工具调用参数（原始 JSON 字符串）</summary>
        public string Input { get; }
        public SdkToolUseBlock(JsonElement block) : base("tool_use")
        {
            Id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            Name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            Input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
        }
    }

    /// <summary>思考内容块 — AI 的推理过程（extended thinking）。</summary>
    public class SdkThinkingBlock : SdkContentBlock
    {
        /// <summary>思考文本内容</summary>
        public string Thinking { get; }
        /// <summary>思考签名（用于验证思考内容完整性）</summary>
        public string? Signature { get; }
        public SdkThinkingBlock(string thinking, string? signature) : base("thinking")
        { Thinking = thinking; Signature = signature; }
    }

    /// <summary>工具结果内容块 — 工具执行结果的回显。</summary>
    public class SdkToolResultBlock : SdkContentBlock
    {
        /// <summary>关联的工具调用 ID</summary>
        public string? ToolUseId { get; }
        /// <summary>工具执行是否为错误结果</summary>
        public bool IsError { get; }
        /// <summary>工具执行返回的内容（文本或数组展开为字符串）</summary>
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

    /// <summary>
    /// SDK 流式事件 — stream_event 消息的 event 字段。
    /// 表示流式回复中的增量变化（块开始 / 增量文本 / 块结束）。
    /// </summary>
    public class SdkStreamEvent
    {
        /// <summary>事件类型：content_block_start / content_block_delta / content_block_stop</summary>
        public string EventType { get; }
        /// <summary>内容块在回复中的索引位置</summary>
        public int Index { get; }
        /// <summary>块的子类型：text / thinking / tool_use（仅 content_block_start 时有值）</summary>
        public string? BlockType { get; }
        /// <summary>文本增量内容（text_delta）</summary>
        public string? Text { get; }
        /// <summary>思考增量内容（thinking_delta）</summary>
        public string? Thinking { get; }
        /// <summary>工具参数 JSON 增量（input_json_delta）</summary>
        public string? PartialJson { get; }
        /// <summary>工具调用 ID（tool_use block_start 时赋值）</summary>
        public string? ToolUseId { get; }
        /// <summary>工具名称（tool_use block_start 时赋值）</summary>
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

        /// <summary>创建文本增量事件</summary>
        public static SdkStreamEvent TextDelta(int idx, string text)
            => new SdkStreamEvent("content_block_delta", idx, null, text: text);
        /// <summary>创建思考增量事件</summary>
        public static SdkStreamEvent ThinkingDelta(int idx, string thinking)
            => new SdkStreamEvent("content_block_delta", idx, null, thinking: thinking);
        /// <summary>创建工具参数 JSON 增量事件</summary>
        public static SdkStreamEvent InputJsonDelta(int idx, string json)
            => new SdkStreamEvent("content_block_delta", idx, null, partialJson: json);
        /// <summary>创建文本块开始事件</summary>
        public static SdkStreamEvent TextBlockStart(int idx)
            => new SdkStreamEvent("content_block_start", idx, "text");
        /// <summary>创建思考块开始事件</summary>
        public static SdkStreamEvent ThinkingBlockStart(int idx)
            => new SdkStreamEvent("content_block_start", idx, "thinking");
        /// <summary>创建工具调用块开始事件</summary>
        public static SdkStreamEvent ToolUseBlockStart(int idx, string? id, string? name)
            => new SdkStreamEvent("content_block_start", idx, "tool_use", toolUseId: id, toolName: name);
    }

    // ===== 辅助类型 =====

    /// <summary>
    /// Token 使用统计。
    /// 包含输入/输出 token 数以及 prompt cache 命中/写入 token 数。
    /// </summary>
    public class SdkUsage
    {
        /// <summary>输入 token 数</summary>
        public long InputTokens { get; }
        /// <summary>输出 token 数</summary>
        public long OutputTokens { get; }
        /// <summary>从 prompt cache 读取的 token 数（缓存命中）</summary>
        public long? CacheReadInputTokens { get; }
        /// <summary>新写入 prompt cache 的 token 数（缓存创建）</summary>
        public long? CacheCreationInputTokens { get; }

        public SdkUsage(JsonElement usage)
        {
            InputTokens = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var n) ? n : 0;
            OutputTokens = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var n2) ? n2 : 0;
            CacheReadInputTokens = usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var n3) ? n3 : (long?)null;
            CacheCreationInputTokens = usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.TryGetInt64(out var n4) ? n4 : (long?)null;
        }
    }

    /// <summary>
    /// MCP 服务器连接信息。
    /// 包含服务器名称和当前连接状态（connected / failed / needs-auth / pending / disabled）。
    /// </summary>
    public class SdkMcpServerInfo
    {
        /// <summary>MCP 服务器名称</summary>
        public string Name { get; }
        /// <summary>当前连接状态：connected / failed / needs-auth / pending / disabled</summary>
        public string Status { get; }
        public SdkMcpServerInfo(string name, string status) { Name = name; Status = status; }
    }
}
