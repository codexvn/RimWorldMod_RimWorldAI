namespace RimWorldMCP
{
    /// <summary>MCP 内部运行状态 — 供 ToolRegistry 注入工具结果使用。Agent 不再直接读写此状态。</summary>
    public static class McpInternalState
    {
        /// <summary>L3 高危事件导致游戏暂停</summary>
        public static bool DangerPaused { get; set; }

        /// <summary>L3 事件摘要文本（供 ToolRegistry 注入工具返回值）</summary>
        public static string DangerSummary { get; set; } = "";

        /// <summary>L1+L2 非高危通知计数（供 ToolRegistry 注入工具返回值）</summary>
        public static int PendingLevel12Count { get; set; }

        public static void ResetPendingLevel12Count()
        {
            PendingLevel12Count = 0;
        }
    }
}
