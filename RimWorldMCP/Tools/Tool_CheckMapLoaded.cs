using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_CheckMapLoaded : ITool, INoMapRequired
    {
        public string Name => "check_map_loaded";
        public string Description => "检查当前游戏和地图加载状态。在执行地图操作前可先调用确认。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                var sb = new StringBuilder();

                if (Current.Game == null)
                {
                    sb.AppendLine("状态: 游戏未启动（主菜单）");
                    sb.AppendLine("提示: 请先开始新游戏或加载存档。");
                    return ToolResult.Success(sb.ToString());
                }

                var map = Find.CurrentMap;
                if (map == null)
                {
                    sb.AppendLine("状态: 游戏已加载，但当前没有进入地图");
                    sb.AppendLine("提示: 可能在世界地图界面，请选择一个定居点进入地图。");
                    return ToolResult.Success(sb.ToString());
                }

                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                int tick = Find.TickManager?.TicksGame ?? 0;
                int day = tick / 60000;
                int hour = (tick / 2500) % 24;

                sb.AppendLine("状态: 地图已加载");
                sb.AppendLine($"地图大小: {map.Size.x}x{map.Size.z}");
                sb.AppendLine($"殖民者: {colonists?.Count ?? 0} 人");
                sb.AppendLine($"游戏时间: Day {day}, {hour:D2}:00");

                return ToolResult.Success(sb.ToString());
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
