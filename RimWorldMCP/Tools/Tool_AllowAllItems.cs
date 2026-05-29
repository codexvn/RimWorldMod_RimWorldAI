using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_AllowAllItems : ITool
    {
        public string Name => "allow_all_items";
        public string Description => "允许地图上所有已被禁止的物品，让殖民者可以搬运和使用。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            @default = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图，请先加载游戏存档。");

                    int allowed = 0, skipped = 0;
                    foreach (Thing thing in map.listerThings.AllThings)
                    {
                        CompForbiddable comp = thing.TryGetComp<CompForbiddable>();
                        if (comp == null) { skipped++; continue; }
                        if (!comp.Forbidden) { skipped++; continue; }

                        comp.Forbidden = false;
                        allowed++;
                    }

                    var sb = new StringBuilder();
                    sb.Append($"已允许 {allowed} 个被禁止的物品");
                    if (skipped > 0)
                        sb.Append($"（{skipped} 个跳过：不可禁止或已允许）");
                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex) { return ToolResult.Error($"允许物品失败: {ex.Message}"); }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
