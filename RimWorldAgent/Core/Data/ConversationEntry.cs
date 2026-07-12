using System;
using System.Text.Json.Serialization;

namespace RimWorldAgent.Core.Data
{
    /// <summary>会话角色</summary>
    public enum ConvRole
    {
        User,
        Assistant,
        System,
        ToolCall,
        ToolResult
    }

    /// <summary>
    /// 单条会话记录 — 独立于 ChatDisplayState/ChatEntry，专用于持久化。
    /// 支持 user/assistant/system/tool_call/tool_result 五种类型。
    /// </summary>
    public class ConversationEntry
    {
        /// <summary>SQLite 自增主键（0 = 未持久化）</summary>
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("role")]
        public ConvRole Role { get; set; }

        /// <summary>消息文本 / tool output 内容</summary>
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        /// <summary>思考文本（仅 assistant）</summary>
        [JsonPropertyName("thinking")]
        public string Thinking { get; set; } = "";

        /// <summary>ACP message ID / tool call ID。</summary>
        [JsonPropertyName("run_id")]
        public string RunId { get; set; } = "";

        /// <summary>子 Agent 类型（空串 = 主 Agent）</summary>
        [JsonPropertyName("agent_type")]
        public string AgentType { get; set; } = "";

        /// <summary>工具名（仅 ToolCall / ToolResult）</summary>
        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = "";

        /// <summary>权限判定用工具名（按 ToolNameJsonPath 从 rawInput 提取）；与 tool_name(action 名) 不同</summary>
        [JsonPropertyName("permission_tool_name")]
        public string PermissionToolName { get; set; } = "";

        /// <summary>工具输入 JSON（仅 ToolCall）</summary>
        [JsonPropertyName("tool_input")]
        public string ToolInput { get; set; } = "";

        /// <summary>工具执行是否失败（仅 ToolResult）</summary>
        [JsonPropertyName("is_tool_error")]
        public bool IsToolError { get; set; }

        /// <summary>工具执行耗时 ms（仅 ToolResult）</summary>
        [JsonPropertyName("tool_duration_ms")]
        public double ToolDurationMs { get; set; }

        /// <summary>UTC 时间戳</summary>
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>游戏内天数（GameTick / 60000）</summary>
        [JsonPropertyName("game_day")]
        public int GameDay { get; set; }

    }

    /// <summary>每日工具调用统计 — 按游戏内天数 + 工具名聚合</summary>
    public class ToolCallDailyStat
    {
        /// <summary>游戏内天数</summary>
        [JsonPropertyName("game_day")]
        public int GameDay { get; set; }

        /// <summary>工具名</summary>
        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = "";

        /// <summary>调用次数</summary>
        [JsonPropertyName("call_count")]
        public int CallCount { get; set; }
    }
}
