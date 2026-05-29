using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetGameContext : ITool
    {
        public string Name => "get_game_context";
        public string Description => "获取 RimWorld 当前游戏的完整状态上下文，包括殖民地概况、资源库存、研究进度、威胁信息、当前工作单等。应在执行任何操作前先调用此工具了解局势。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, required = System.Array.Empty<string>() });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
                ToolResult.Success(GameContextProvider.BuildGameContext()));
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
