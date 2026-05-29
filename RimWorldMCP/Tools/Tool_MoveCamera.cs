using System;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_MoveCamera : ITool
    {
        public string Name => "move_camera";
        public string Description => "移动游戏视角到指定地图坐标。利用游戏 CameraDriver.PanToMapLoc，视角平滑滑动，速度根据距离自动调整。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "目标 X 坐标（水平网格轴）" },
                pos_y = new { type = "integer", description = "目标 Y 坐标（垂直网格轴，映射到 IntVec3.z）" }
            },
            required = new[] { "pos_x", "pos_y" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                return ToolResult.Error("缺少必填参数: pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                return ToolResult.Error("缺少必填参数: pos_y");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    if (posX < 0 || posX >= map.Size.x || posY < 0 || posY >= map.Size.z)
                        return ToolResult.Error($"目标坐标 ({posX},{posY}) 超出地图边界 (0~{map.Size.x - 1}, 0~{map.Size.z - 1})");

                    var cell = new IntVec3(posX, 0, posY);
                    Find.CameraDriver.PanToMapLoc(cell);

                    return ToolResult.Success($"视角已移动到 ({posX}, {posY})。");
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"移动视角失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
