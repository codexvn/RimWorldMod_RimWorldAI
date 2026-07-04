using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using RimWorldMCP.Compression;
using RimWorldMCP.MapRendering;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_PollutionGrid : ITool
    {
        public string Name => "pollution_grid";
        public string Description => "获取指定区域的污染网格（支持 Chunk 或坐标两种查询模式）。需要 Biotech DLC。坐标范围为闭区间（两端坐标均包含）。地图坐标系: 左下角为原点(0,0)，x向东、z向北。网格上北下南、左西右东。";

        private const int MaxGridWidth = 80;
        private const int MaxGridHeight = 60;

        public JsonElement InputSchema
        {
            get
            {
                var mode = RimWorldMCPMod.Instance?.Settings?.GridQueryMode ?? GridQueryMode.Chunk;
                if (mode == GridQueryMode.Chunk)
                {
                    return JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new
                        {
                            chunk_id = new { type = "string", description = "Chunk ID，格式 \"X_Z\"" }
                        },
                        required = new[] { "chunk_id" }
                    });
                }
                else
                {
                    return JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new
                        {
                            pos_x = new { type = "integer", description = "左下角 X 坐标（闭区间，含此坐标）" },
                            pos_y = new { type = "integer", description = "左下角 Y 坐标（闭区间，含此坐标）" },
                            end_x = new { type = "integer", description = "右上角 X 坐标（可选，闭区间，含此坐标）" },
                            end_y = new { type = "integer", description = "右上角 Y 坐标（可选，闭区间，含此坐标）" }
                        },
                        required = new[] { "pos_x", "pos_y" }
                    });
                }
            }
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            var mode = RimWorldMCPMod.Instance?.Settings?.GridQueryMode ?? GridQueryMode.Chunk;

            if (mode == GridQueryMode.Chunk)
            {
                if (!args.Value.TryGetProperty("chunk_id", out var jId))
                    return ToolResult.Error("缺少必填参数: chunk_id");
                var chunkId = jId.GetString();
                if (!MapChunker.TryParseChunkId(chunkId ?? "", out int xIndex, out int zIndex))
                    return ToolResult.Error($"无效的 chunk_id: {chunkId}（格式: X_Z）");

                return await McpCommandQueue.DispatchAsync(() =>
                {
                    try
                    {
                        var map = Find.CurrentMap;
                        if (map == null) return ToolResult.Error("当前没有可用地图。");
                        if (!ModsConfig.BiotechActive) return ToolResult.Error("需要 Biotech DLC 才能查询污染层。");

                        var settings = RimWorldMCPMod.Instance?.Settings;
                        int cw = settings?.ChunkWidth ?? 32;
                        int ch = settings?.ChunkHeight ?? 32;
                        var method = settings?.GridCompression ?? CompressionMethod.RLE;

                        var chunk = MapChunker.GetChunkByIndex(xIndex, zIndex, map.Size.x, map.Size.z, cw, ch);
                        var compressor = CompressorFactory.Create(method);

                        var result = GridRenderer.RenderGrid(map, chunk.MinX, chunk.MinZ, chunk.MaxX, chunk.MaxZ,
                            CellCharProviders.ForPollution);

                        int polluted = 0, total = 0;
                        foreach (var row in result.Rows)
                            foreach (var c in row)
                            {
                                total++;
                                if (c == 'P') polluted++;
                            }

                        Array.Reverse(result.Rows); // 翻转行序：高z（北）先输出
                        chunk.CompressedData = compressor.Compress(result.Rows, (chunk.XIndex, chunk.ZIndex));

                        var sb = new StringBuilder();
                        sb.AppendLine($"## {Name}  Chunk({chunk.XIndex},{chunk.ZIndex})  x∈[{chunk.MinX},{chunk.MaxX}] z∈[{chunk.MinZ},{chunk.MaxZ}]  {chunk.Width}×{chunk.Height}");
                        sb.AppendLine($"## 压缩: {compressor.Name}");
                        sb.AppendLine();
                        sb.AppendLine(chunk.CompressedData);
                        sb.AppendLine();
                        sb.AppendLine($"## 图例  P污染  .干净  ?迷雾  | 污染率: {polluted}/{total} ({100 * polluted / total}%)");

                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }
                    catch (Exception ex) { return ToolResult.Error($"污染查询失败: {ex.Message}"); }
                });
            }
            else
            {
                if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX))
                    return ToolResult.Error("缺少必填参数: pos_x");
                if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY))
                    return ToolResult.Error("缺少必填参数: pos_y");

                int endX = posX, endY = posY;
                if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
                if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;

                int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
                int minZ = Math.Min(posY, endY), maxZ = Math.Max(posY, endY);

                int w = maxX - minX + 1;
                int h = maxZ - minZ + 1;
                if (w > MaxGridWidth || h > MaxGridHeight)
                    return ToolResult.Error($"查询范围 {w}x{h} 超过上限 {MaxGridWidth}x{MaxGridHeight}，请缩小范围。");

                return await McpCommandQueue.DispatchAsync(() =>
                {
                    try
                    {
                        var map = Find.CurrentMap;
                        if (map == null) return ToolResult.Error("当前没有可用地图。");
                        if (!ModsConfig.BiotechActive) return ToolResult.Error("需要 Biotech DLC 才能查询污染层。");

                        var result = GridRenderer.RenderGrid(map, minX, minZ, maxX, maxZ,
                            CellCharProviders.ForPollution);

                        int polluted = 0, total = 0;
                        foreach (var row in result.Rows)
                            foreach (var c in row)
                            {
                                total++;
                                if (c == 'P') polluted++;
                            }

                        var sb = new StringBuilder();
                        sb.AppendLine($"## {Name}  x∈[{minX},{maxX}] z∈[{minZ},{maxZ}]  {w}×{h}");
                        sb.AppendLine();

                        for (int z = h - 1; z >= 0; z--)
                        {
                            sb.Append($"z{minZ + z}: ");
                            sb.AppendLine(new string(result.Rows[z]));
                        }

                        sb.AppendLine();
                        sb.AppendLine($"## 图例  P污染  .干净  ?迷雾  | 污染率: {polluted}/{total} ({100 * polluted / total}%)");

                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }
                    catch (Exception ex) { return ToolResult.Error($"污染查询失败: {ex.Message}"); }
                });
            }
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;

            var mode = RimWorldMCPMod.Instance?.Settings?.GridQueryMode ?? GridQueryMode.Chunk;

            if (mode == GridQueryMode.Chunk)
            {
                if (!args.Value.TryGetProperty("chunk_id", out var jId)) return null;
                var chunkId = jId.GetString();
                if (!MapChunker.TryParseChunkId(chunkId ?? "", out int xIndex, out int zIndex)) return null;
                var map = Find.CurrentMap;
                if (map == null) return null;
                var settings = RimWorldMCPMod.Instance?.Settings;
                int cw = settings?.ChunkWidth ?? 32;
                int ch = settings?.ChunkHeight ?? 32;
                var chunk = MapChunker.GetChunkByIndex(xIndex, zIndex, map.Size.x, map.Size.z, cw, ch);
                return (chunk.MinX, chunk.MinZ, chunk.MaxX, chunk.MaxZ);
            }
            else
            {
                if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
                if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
                int endX = posX, endY = posY;
                if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var ex)) endX = ex;
                if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var ey)) endY = ey;
                return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
            }
        }
    }
}
