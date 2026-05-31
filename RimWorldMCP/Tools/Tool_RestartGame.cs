using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.Profile;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_RestartGame : ITool, INoMapRequired
    {
        public string Name => "restart_game";

        public string Description => "使用开发者快速测试功能重新开始游戏（全新 Crashlanded 开局，Cassandra Classic / Rough 难度，随机世界+地块）。"
            + "当前游戏的所有进度将丢失。需要 i_know_danger 确认。可选地图大小参数。"
            + "此工具会阻塞直到新游戏完全加载完毕。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                i_know_danger = new
                {
                    type = "boolean",
                    description = "确认了解此操作会销毁当前游戏的所有殖民地、殖民者、建筑和物品，创建全新的 Crashlanded 开局。必须设为 true。"
                },
                map_size = new
                {
                    type = "integer",
                    description = "新地图边长（可选，50~400）。默认 250。"
                }
            },
            required = new[] { "i_know_danger" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 解析参数
            int mapSize = 250;
            if (args != null && args.Value.TryGetProperty("map_size", out var mp))
            {
                if (mp.TryGetInt32(out var v))
                    mapSize = v;
                if (mapSize < 50 || mapSize > 400)
                    return ToolResult.Error("map_size 必须在 50~400 之间。");
            }

            // 安全确认
            bool confirmed = false;
            if (args != null && args.Value.TryGetProperty("i_know_danger", out var d)
                && d.ValueKind == JsonValueKind.True)
                confirmed = true;

            if (!confirmed)
                return ToolResult.Error("必须设置 i_know_danger = true 以确认了解风险。"
                    + "此操作将销毁当前游戏的所有殖民地、殖民者、建筑和物品，"
                    + "并使用 Dev Quick Test 创建全新的 Crashlanded 开局游戏！");

            // 调度重启到主线程，完成后阻塞等待游戏完全加载
            var restartOk = await McpCommandQueue.DispatchAsync(() =>
            {
                if (Current.Game == null)
                    return "ERR:当前没有游戏实例，无法重新开始。";

                try { PerformGameRestart(mapSize); }
                catch (Exception ex) { return $"ERR:{ex.Message}"; }
                return "OK";
            });

            if (restartOk is string s && s.StartsWith("ERR:"))
                return ToolResult.Error(s.Substring(4));

            // 阻塞等待新游戏完全加载（最多 120 秒）
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                var state = await McpCommandQueue.DispatchAsync(() =>
                {
                    if (Current.Game == null) return "menu";
                    var map = Find.CurrentMap;
                    if (map == null) return "world";
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0) return "loading";
                    return $"ready:{colonists.Count}:{map.Size.x}x{map.Size.z}";
                });

                if (state.StartsWith("ready:"))
                {
                    var parts = state.Split(':');
                    var sb = new StringBuilder();
                    sb.AppendLine("游戏已重新开始！");
                    sb.AppendLine();
                    sb.AppendLine("【新游戏信息】");
                    sb.AppendLine($"- 殖民者: {parts[1]} 人");
                    sb.AppendLine($"- 地图大小: {parts[2]}");
                    sb.AppendLine($"- 剧本: Crashlanded（坠毁三人组）");
                    sb.AppendLine($"- AI 叙事者: Cassandra Classic / Rough");
                    sb.AppendLine($"- 地图边长: {mapSize}");
                    return ToolResult.Success(sb.ToString());
                }

                await Task.Delay(500);
            }

            return ToolResult.Error("游戏重启超时 (120s)：新游戏加载未完成，请用 check_map_loaded 检查当前状态。");
        }

        private static void PerformGameRestart(int mapSize)
        {
            McpLog.Info("开始重新开始游戏（Dev Quick Test）...");

            // 1. 清空待处理的长时间事件
            LongEventHandler.ClearQueuedEvents();

            // 2. 清理旧游戏缓存
            Game.ClearCaches();
            MemoryUtility.ClearAllMapsAndWorld();

            // 3. 创建新游戏（Dev Quick Test 配置）
            Root_Play.SetupForQuickTestPlay();

            // 4. 覆盖地图大小
            if (mapSize != 250)
                Find.GameInitData.mapSize = mapSize;

            // 5. 初始化新游戏
            Find.GameInitData.PrepForMapGen();
            Find.Scenario.PreMapGenerate();
            Current.Game.InitNewGame();
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
