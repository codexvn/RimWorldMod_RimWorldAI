using System.Collections.Generic;

namespace RimWorldAgent.Core.Data
{
    /// <summary>
    /// 会话历史存储抽象。
    /// 所有实现必须线程安全（支持并发读写）。
    /// </summary>
    public interface IConversationStore
    {
        /// <summary>已存储条目总数</summary>
        int Count { get; }

        /// <summary>记录用户消息</summary>
        void RecordUserMessage(string text);

        /// <summary>记录 AI 回复（完整 text + thinking）</summary>
        void RecordAssistantMessage(string text, string thinking, string runId, string agentType);

        /// <summary>记录系统消息（暂停提醒、系统 prompt、错误等）</summary>
        void RecordSystemMessage(string text);

        /// <summary>记录工具调用</summary>
        void RecordToolCall(string toolId, string name, string input);

        /// <summary>记录工具执行结果</summary>
        void RecordToolResult(string toolId, bool isError, double durationMs, string output);

        /// <summary>按主键 ID 精确查询，不存在返回 null</summary>
        ConversationEntry? GetAt(long id);

        /// <summary>获取最近 n 条（按时间升序）</summary>
        IReadOnlyList<ConversationEntry> GetRecent(int n);

        /// <summary>获取指定 ID 之前的 n 条（按时间升序），用于向上滚动加载更早消息</summary>
        IReadOnlyList<ConversationEntry> GetBefore(long beforeId, int n);

        /// <summary>按工具名/游戏日范围查询 tool_call 记录，支持分页</summary>
        IReadOnlyList<ConversationEntry> QueryToolCalls(
            string? toolName = null,
            int fromDay = 0,
            int toDay = int.MaxValue,
            int limit = 100,
            long beforeId = long.MaxValue);

        /// <summary>每日工具调用次数统计（按游戏内天数 GROUP BY）</summary>
        IReadOnlyList<ToolCallDailyStat> GetToolDailyStats(
            int fromDay = 0,
            int toDay = int.MaxValue);

        /// <summary>获取所有已知工具名列表（供 UI 下拉筛选）</summary>
        List<string> GetKnownToolNames();
    }
}
