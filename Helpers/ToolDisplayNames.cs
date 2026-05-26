using Verse;

namespace RimWorldMCP.Helpers
{
    /// <summary>工具 ID → 显示名映射，使用 RimWorld 内置多语言系统</summary>
    public static class ToolDisplayNames
    {
        private const string TranslationKeyPrefix = "RimWorldMCP_Tool_";

        /// <summary>获取工具的本地化显示名。CC 传来的格式为 mcp__{server}__{tool}，取最后一段查翻译表。</summary>
        public static string GetDisplayName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";

            // mcp__rimworld__get_colonists → get_colonists
            var lastSep = rawName.LastIndexOf("__");
            var toolName = lastSep >= 0 ? rawName.Substring(lastSep + 2) : rawName;

            return (TranslationKeyPrefix + toolName).Translate();
        }
    }
}
