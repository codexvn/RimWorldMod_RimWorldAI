using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.MapRendering;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_ListChunks : ITool
    {
        public string Name => "list_chunks";
        public string Description => "获取指定矩形范围覆盖的 chunk ID 列表（行主序）。用于在查询地图前了解需要哪些 chunk。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左上 X 坐标" },
                pos_y = new { type = "integer", description = "左上 Y 坐标" },
                end_x = new { type = "integer", description = "右下 X 坐标（可选，默认=pos_x）" },
                end_y = new { type = "integer", description = "右下 Y 坐标（可选，默认=pos_y）" }
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

            int endX = posX, endY = posY;
            if (args.Value.TryGetProperty("end_x", out var jEx)) jEx.TryGetInt32(out endX);
            if (args.Value.TryGetProperty("end_y", out var jEy)) jEy.TryGetInt32(out endY);

            int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
            int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var settings = RimWorldMCPMod.Instance?.Settings;
                    int cw = settings?.ChunkWidth ?? 32;
                    int ch = settings?.ChunkHeight ?? 32;

                    int mapW = map.Size.x, mapH = map.Size.z;
                    if (minX < 0) minX = 0;
                    if (minZ < 0) minZ = 0;
                    if (maxX >= mapW) maxX = mapW - 1;
                    if (maxZ >= mapH) maxZ = mapH - 1;

                    var chunks = MapChunker.GetIntersectingChunks(minX, minZ, maxX, maxZ,
                        mapW, mapH, cw, ch);

                    var (cols, rows) = MapChunker.GetGridDimensions(mapW, mapH, cw, ch);

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 矩形 ({minX},{minZ})~({maxX},{maxZ}) 覆盖 Chunk 列表");
                    sb.AppendLine($"## 分块: {cw}x{ch}  地图: {mapW}x{mapH}  分块网格: {cols}x{rows}  行主序");
                    sb.AppendLine();

                    foreach (var chunk in chunks)
                        sb.AppendLine($"Chunk({chunk.XIndex},{chunk.ZIndex}) ({chunk.MinX},{chunk.MinZ})-({chunk.MaxX},{chunk.MaxZ})");

                    sb.AppendLine();
                    sb.AppendLine("## chunk_id 列表");
                    foreach (var chunk in chunks)
                        sb.AppendLine(MapChunker.FormatChunkId(chunk.XIndex, chunk.ZIndex));

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex) { return ToolResult.Error($"查询失败: {ex.Message}"); }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var x)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var y)) return null;
            int ex = x, ey = y;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var _ex)) ex = _ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var _ey)) ey = _ey;
            return (Math.Min(x, ex), Math.Min(y, ey), Math.Max(x, ex), Math.Max(y, ey));
        }
    }
}
