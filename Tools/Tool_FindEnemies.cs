using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_FindEnemies : ITool
    {
        public string Name => "find_enemies";
        public string Description => "查找地图上所有敌对目标，输出 thingIDNumber、名称、坐标，用于后续 attack_pawn / force_attack 精确指定目标。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var playerFaction = Faction.OfPlayer;
                    if (playerFaction == null) return ToolResult.Error("无法获取玩家派系。");

                    var enemies = map.mapPawns.AllPawnsSpawned
                        .Where(p => p.HostileTo(playerFaction) && !p.Dead && !p.Destroyed)
                        .OrderBy(p => p.Position.x)
                        .ThenBy(p => p.Position.z)
                        .ToList();

                    if (enemies.Count == 0)
                        return ToolResult.Success("地图上没有发现敌对目标。");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 敌对目标 ({enemies.Count})");
                    sb.AppendLine("| thingIDNumber | 名称 | 类型 | 坐标 | 状态 |");
                    sb.AppendLine("|---|---:|---:|---|");
                    foreach (var e in enemies)
                    {
                        string status = e.Downed ? "倒地" : e.Drafted ? "征召" : "活跃";
                        sb.AppendLine($"| {e.thingIDNumber} | {e.LabelShort} | {e.KindLabel} | ({e.Position.x},{e.Position.z}) | {status} |");
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"查找敌人失败: {ex.Message}");
                }
            });
        }
    }
}
