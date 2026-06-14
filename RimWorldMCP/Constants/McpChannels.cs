namespace RimWorldMCP.Constants
{
    /// <summary>MCP SSE 推送通道名常量（发送侧）</summary>
    public static class McpChannels
    {
        /// <summary>游戏 tick 推送（每 N tick）</summary>
        public const string GameTick = "game/tick";

        /// <summary>通知事件（Letter / Message / Alert 等）</summary>
        public const string GameNotification = "game/notification";

        /// <summary>物品腐坏/耐久降低警告</summary>
        public const string GameDeterioration = "game/deterioration";

        /// <summary>殖民者被困警告</summary>
        public const string GameTrapped = "game/trapped";

        /// <summary>战斗事件（远程/近战/状态变更，含伤害数值）</summary>
        public const string GameCombat = "game/combat";
    }
}
