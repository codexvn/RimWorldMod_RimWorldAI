using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorld.Planet;

namespace RimWorldMCP.Tools
{
    public class Tool_RegenerateMap : ITool
    {
        public string Name => "regenerate_map";

        public string Description => "立即销毁并重新生成当前地图（开发者模式 RegenerateCurrentMap 调试动作）。"
            + "保留地块位置，地图大小可指定。注意：此操作不可逆，地图上所有物品、建筑、生物和地形修改都将丢失！";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                i_know_danger = new
                {
                    type = "boolean",
                    description = "确认了解此操作不可逆，地图上所有物品、建筑、生物将被永久删除。必须设为 true。"
                },
                new_map_size = new
                {
                    type = "integer",
                    description = "新地图边长（可选，50~400）。不传则保持原地图大小。"
                }
            },
            required = new[] { "i_know_danger" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            int? newSize = null;
            if (args != null && args.Value.TryGetProperty("new_map_size", out var sizeProp))
            {
                newSize = sizeProp.GetInt32();
                if (newSize < 50 || newSize > 400)
                    return ToolResult.Error("new_map_size 必须在 50~400 之间。");
            }

            // 安全确认：必须显式传入 i_know_danger: true
            bool confirmed = false;
            if (args != null && args.Value.TryGetProperty("i_know_danger", out var dangerProp) && dangerProp.ValueKind == JsonValueKind.True)
                confirmed = true;

            if (!confirmed)
                return ToolResult.Error("必须设置 i_know_danger = true 以确认了解风险。此操作不可逆，地图上所有物品、建筑和生物都将被永久删除！");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    if (Find.CurrentMap == null)
                        return ToolResult.Error("当前没有地图。");

                    // 1. 保存当前地图参数
                    var rememberedCameraPos = Find.CurrentMap.rememberedCameraPos;
                    var tile = Find.CurrentMap.Tile;
                    var parent = Find.CurrentMap.Parent;
                    var size = newSize.HasValue ? new IntVec3(newSize.Value, 1, newSize.Value) : Find.CurrentMap.Size;
                    var isPocketMap = Find.CurrentMap.IsPocketMap;

                    // 2. 销毁当前地图
                    Current.Game.DeinitAndRemoveMap(Find.CurrentMap, true);

                    // 3. 重新生成地图
                    Map newMap;
                    if (isPocketMap)
                        newMap = PocketMapUtility.GeneratePocketMap(size, parent.MapGeneratorDef, null, Find.AnyPlayerHomeMap);
                    else
                        newMap = GetOrGenerateMapUtility.GetOrGenerateMap(tile, size, parent.def, null, false);

                    Current.Game.CurrentMap = newMap;

                    // 4. 隐藏世界地图，恢复镜头位置
                    Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
                    Find.CameraDriver.SetRootPosAndSize(rememberedCameraPos.rootPos, rememberedCameraPos.rootSize);

                    // 5. 返回结果
                    var sb = new StringBuilder();
                    sb.AppendLine("地图已重新生成！");
                    sb.AppendLine($"- 尺寸: {size.x} x {size.z}");
                    sb.AppendLine($"- 地块: {tile.tileId} ({Find.WorldGrid[tile].PrimaryBiome?.label ?? "未知"})");
                    sb.AppendLine($"- 口袋地图: {(isPocketMap ? "是" : "否")}");
                    sb.AppendLine();
                    sb.AppendLine("注意：原地图上的所有建筑、物品和生物已丢失。");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"地图重新生成失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
