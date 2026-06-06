using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// ConversationEntry → 前端 JSON 格式转换。
    /// 输出格式与 index.html addMsgSimple / handleMessage('history_response') 对齐。
    /// </summary>
    public static class UiHistoryFormatter
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>将条目列表格式化为 WS history_response JSON 字符串</summary>
        public static string FormatResponse(IReadOnlyList<ConversationEntry> entries)
        {
            var doc = BuildResponseDocument(entries, "history_response");
            return JsonSerializer.Serialize(doc, Options);
        }

        /// <summary>将更早的消息格式化为 WS history_before_response JSON 字符串</summary>
        public static string FormatBeforeResponse(IReadOnlyList<ConversationEntry> entries, bool hasMore)
        {
            var messages = new List<object>(entries.Count);
            foreach (var entry in entries)
                messages.Add(FormatEntry(entry));
            var doc = new { type = "history_before_response", messages, has_more = hasMore };
            return JsonSerializer.Serialize(doc, Options);
        }

        /// <summary>将工具统计格式化为 WS tool_stats_response JSON 字符串</summary>
        public static string FormatToolStatsResponse(IReadOnlyList<ToolCallDailyStat> stats, int gameDay)
        {
            var doc = new
            {
                type = "tool_stats_response",
                game_day = gameDay,
                stats = stats
            };
            return JsonSerializer.Serialize(doc, Options);
        }

        private static object BuildResponseDocument(IReadOnlyList<ConversationEntry> entries, string responseType)
        {
            var messages = new List<object>(entries.Count);
            foreach (var entry in entries)
            {
                messages.Add(FormatEntry(entry));
            }
            return new { type = responseType, messages };
        }

        private static object FormatEntry(ConversationEntry entry)
        {
            var contentType = entry.Role switch
            {
                ConvRole.User => "user",
                ConvRole.Assistant => "assistant",
                ConvRole.System => "assistant",
                ConvRole.ToolCall => "tool_call",
                ConvRole.ToolResult => "tool_result",
                _ => "assistant"
            };

            if (entry.Role == ConvRole.ToolCall)
            {
                return new
                {
                    type = "tool_call",
                    id = entry.RunId,
                    name = entry.ToolName ?? "",
                    input = entry.ToolInput ?? ""
                };
            }

            if (entry.Role == ConvRole.ToolResult)
            {
                return new
                {
                    type = "tool_result",
                    id = entry.RunId,
                    tool_use_id = entry.RunId ?? "",
                    is_error = entry.IsToolError,
                    durationMs = entry.ToolDurationMs,
                    content = entry.Text ?? ""
                };
            }

            // 构建 content 数组
            var content = new List<object>(2);
            if (!string.IsNullOrEmpty(entry.Thinking))
                content.Add(new { type = "thinking", thinking = entry.Thinking });
            if (!string.IsNullOrEmpty(entry.Text))
                content.Add(new { type = "text", text = entry.Text });

            return new
            {
                type = contentType,
                id = entry.Id,
                uuid = string.IsNullOrEmpty(entry.RunId) ? $"msg_{entry.Id}" : entry.RunId,
                agent_type = entry.AgentType ?? "",
                message = new { content }
            };
        }
    }
}
