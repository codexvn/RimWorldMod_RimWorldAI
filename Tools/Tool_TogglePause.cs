using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TogglePause : ITool
    {
        public string Name => "toggle_pause";
        public string Description => "切换游戏暂停状态。如果已暂停则恢复，如果运行中则暂停。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new object(),
            required = new string[] { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Find.TickManager.TogglePaused();
                    bool nowPaused = Find.TickManager.Paused;
                    string state = nowPaused ? "已暂停" : "运行中";
                    return ToolResult.Success($"游戏当前{state}。");
                }
                catch (System.Exception ex)
                {
                    return ToolResult.Error($"切换暂停失败: {ex.Message}");
                }
            });
        }
    }
}
