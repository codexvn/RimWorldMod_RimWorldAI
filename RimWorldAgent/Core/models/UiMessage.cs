using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldAgent.Core
{
    /// <summary>UI 显示消息基类 — UIMessageBus 统一输出，所有客户端消费同一协议</summary>
    public abstract class UiMessage
    {
        /// <summary>序列化为 WS 广播 JSON</summary>
        public string ToJson() => JsonSerializer.Serialize(this, GetType());

        // ===== 工厂（便捷） =====
        public static UiTextDelta TextDelta(string text) => new UiTextDelta(text);
        public static UiThinkingDelta ThinkingDelta(string thinking) => new UiThinkingDelta(thinking);
        public static UiTextBlock TextBlock(string text) => new UiTextBlock(text);
        public static UiToolCall ToolCall(string id, string name, string input) => new UiToolCall(id, name, input);
        public static UiToolResult ToolResult(string id, bool isError, double durationMs, string? content = null) => new UiToolResult(id, isError, durationMs, content);
        public static UiResult Result(string subtype, string? stopReason) => new UiResult(subtype, stopReason ?? "");
        public static UiAborted Aborted() => new UiAborted();
        public static UiSystemInit SystemInit(string? model, string? sessionId, string? claudeCodeVersion = null,
            string? permissionMode = null, string? apiKeySource = null, List<UiMcpServerRef>? mcpServers = null,
            List<string>? tools = null, List<string>? skills = null)
            => new UiSystemInit(model, sessionId, claudeCodeVersion, permissionMode, apiKeySource,
                mcpServers ?? new List<UiMcpServerRef>(), tools ?? new List<string>(), skills ?? new List<string>());
        public static UiError Error(string error) => new UiError(error);
        public static UiUser User(string text) => new UiUser(text);
        public static UiSystem System(string text) => new UiSystem(text);
        public static UiBudgetStatus BudgetStatus(long used, long limit, string action, long cacheRead, long totalInput, long cacheCreate, long contextWindow = 0, long inputTokens = 0)
            => new UiBudgetStatus(used, limit, action, cacheRead, totalInput, cacheCreate, contextWindow, inputTokens);
        public static UiAgentStatus AgentStatus(string role) => new UiAgentStatus(role);
        public static UiCompactionStatus CompactionStatus(bool active) => new UiCompactionStatus(active);
    }

    // ===== 具体消息类型（与 WS 协议 JSON 字段对齐） =====

    public class UiTextDelta : UiMessage
    {
        public string type => "text_delta";
        public string text { get; }
        public UiTextDelta(string text) { this.text = text; }
    }

    public class UiThinkingDelta : UiMessage
    {
        public string type => "thinking_delta";
        public string thinking { get; }
        public UiThinkingDelta(string thinking) { this.thinking = thinking; }
    }

    public class UiTextBlock : UiMessage
    {
        public string type => "text_block";
        public string text { get; }
        public UiTextBlock(string text) { this.text = text; }
    }

    public class UiToolCall : UiMessage
    {
        public string type => "tool_call";
        public string id { get; }
        public string name { get; }
        public string input { get; }
        public UiToolCall(string id, string name, string input)
        { this.id = id; this.name = name; this.input = input; }
    }

    public class UiToolResult : UiMessage
    {
        public string type => "tool_result";
        public string id { get; }
        public bool isError { get; }
        public double durationMs { get; }
        public string? content { get; }
        public UiToolResult(string id, bool isError, double durationMs, string? content = null)
        { this.id = id; this.isError = isError; this.durationMs = durationMs; this.content = content; }
    }

    public class UiResult : UiMessage
    {
        public string type => "result";
        public string subtype { get; }
        public string stop_reason { get; }
        public UiResult(string subtype, string stopReason)
        { this.subtype = subtype; this.stop_reason = stopReason; }
    }

    public class UiAborted : UiMessage { public string type => "aborted"; }

    public class UiSystemInit : UiMessage
    {
        public string type => "system_init";
        public string? model { get; }
        public string? session_id { get; }
        public string? claude_code_version { get; }
        public string? permissionMode { get; }
        public string? apiKeySource { get; }
        public List<UiMcpServerRef> mcp_servers { get; }
        public List<string> tools { get; }
        public List<string> skills { get; }
        public UiSystemInit(string? model, string? sessionId, string? claudeCodeVersion, string? permissionMode,
            string? apiKeySource, List<UiMcpServerRef> mcpServers, List<string> tools, List<string> skills)
        { this.model = model; this.session_id = sessionId; this.claude_code_version = claudeCodeVersion;
          this.permissionMode = permissionMode; this.apiKeySource = apiKeySource;
          this.mcp_servers = mcpServers; this.tools = tools; this.skills = skills; }
    }

    /// <summary>MCP 服务器引用（system_init 中 mcp_servers 数组元素）</summary>
    public class UiMcpServerRef
    {
        public string name { get; }
        public string status { get; }
        public UiMcpServerRef(string name, string status) { this.name = name; this.status = status; }
    }

    public class UiError : UiMessage
    {
        public string type => "error";
        public string error { get; }
        public UiError(string error) { this.error = error; }
    }

    public class UiUser : UiMessage
    {
        public string type => "user";
        public string text { get; }
        public UiUser(string text) { this.text = text; }
    }

    public class UiSystem : UiMessage
    {
        public string type => "system";
        public string text { get; }
        public UiSystem(string text) { this.text = text; }
    }

    public class UiBudgetStatus : UiMessage
    {
        public string type => "budget_status";
        public long used { get; }
        public long limit { get; }
        public string action { get; }
        public long cacheRead { get; }
        public long totalInput { get; }
        public long cacheCreate { get; }
        public long contextWindow { get; }
        public long inputTokens { get; }
        public UiBudgetStatus(long used, long limit, string action, long cacheRead, long totalInput, long cacheCreate, long contextWindow = 0, long inputTokens = 0)
        { this.used = used; this.limit = limit; this.action = action; this.cacheRead = cacheRead; this.totalInput = totalInput; this.cacheCreate = cacheCreate; this.contextWindow = contextWindow; this.inputTokens = inputTokens; }
    }

    public class UiAgentStatus : UiMessage
    {
        public string type => "agent-status";
        public string role { get; }
        public UiAgentStatus(string role) { this.role = role; }
    }

    /// <summary>SDK 上下文压缩状态 — active=true 表示正在压缩中</summary>
    public class UiCompactionStatus : UiMessage
    {
        public string type => "compaction-status";
        public bool active { get; }
        public UiCompactionStatus(bool active) { this.active = active; }
    }
}
