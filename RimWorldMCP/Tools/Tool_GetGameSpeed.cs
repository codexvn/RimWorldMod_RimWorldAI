using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_GetGameSpeed : ITool, INoMapRequired
    {
        public string Name => "get_game_speed";
        public string Description => "获取当前游戏速度状态（已暂停/1 倍速/2 倍速/3 倍速/最快）。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        /// <summary>查询真实暂停状态（供 Agent 侧同步 PaceController 用）</summary>
        public static bool IsPaused()
        {
            var tm = Find.TickManager;
            return tm != null && tm.Paused;
        }

        /// <summary>获取速度标签文本（供所有模块统一使用）</summary>
        public static string GetSpeedLabel()
        {
            var tm = Find.TickManager;
            if (tm == null) return "未知";

            if (tm.Paused) return "已暂停";

            return tm.CurTimeSpeed switch
            {
                TimeSpeed.Normal => "1 倍速",
                TimeSpeed.Fast => "2 倍速",
                TimeSpeed.Superfast => "3 倍速",
                TimeSpeed.Ultrafast => "最快",
                _ => tm.CurTimeSpeed.ToString()
            };
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
                ToolResult.Success(GetSpeedLabel()));
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
