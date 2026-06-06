using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Compression;
using RimWorldMCP.MapRendering;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_TerrainGrid : ITool
    {
        public string Name => "terrain_grid";
        public string Description => "获取指定区域的地形类型网格（支持 Chunk 或坐标两种查询模式）。坐标范围为闭区间（两端坐标均包含）。地图坐标系: 左下角为原点(0,0)，x向东、z向北。网格上北下南、左西右东。";

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

                        var settings = RimWorldMCPMod.Instance?.Settings;
                        int cw = settings?.ChunkWidth ?? 32;
                        int ch = settings?.ChunkHeight ?? 32;
                        var method = settings?.GridCompression ?? CompressionMethod.RLE;

                        var chunk = MapChunker.GetChunkByIndex(xIndex, zIndex, map.Size.x, map.Size.z, cw, ch);
                        var compressor = CompressorFactory.Create(method);
                        var usedSymbols = new HashSet<char>();

                        var rows = new char[chunk.Height][];
                        for (int z = 0; z < chunk.Height; z++)
                        {
                            rows[z] = new char[chunk.Width];
                            for (int x = 0; x < chunk.Width; x++)
                            {
                                var pos = new IntVec3(chunk.MinX + x, 0, chunk.MinZ + z);
                                var (symbol, _) = CellCharProviders.ForTerrain(pos, map);
                                rows[z][x] = symbol;
                                usedSymbols.Add(symbol);
                            }
                        }

                        Array.Reverse(rows); // 翻转行序：高z（北）先输出
                        chunk.CompressedData = compressor.Compress(rows, (chunk.XIndex, chunk.ZIndex));

                        var sb = new StringBuilder();
                        sb.AppendLine($"## {Name}  Chunk({chunk.XIndex},{chunk.ZIndex})  x∈[{chunk.MinX},{chunk.MaxX}] z∈[{chunk.MinZ},{chunk.MaxZ}]  {chunk.Width}×{chunk.Height}");
                        sb.AppendLine($"## 压缩: {compressor.Name}  字典Hash: {SymbolDictionary.DictHash}");
                        sb.AppendLine();
                        sb.AppendLine(chunk.CompressedData);
                        sb.AppendLine();
                        sb.AppendLine("## 图例");
                        sb.AppendLine(SymbolDictionary.GetLegendString(usedSymbols));

                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }
                    catch (Exception ex) { return ToolResult.Error($"地形查询失败: {ex.Message}"); }
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

                        var result = GridRenderer.RenderGrid(map, minX, minZ, maxX, maxZ, CellCharProviders.ForTerrain);

                        var sb = new StringBuilder();
                        sb.AppendLine($"## {Name}  x∈[{minX},{maxX}] z∈[{minZ},{maxZ}]  {w}×{h}");
                        sb.AppendLine($"## 字典Hash: {SymbolDictionary.DictHash}");
                        sb.AppendLine();

                        for (int z = h - 1; z >= 0; z--)
                        {
                            sb.Append($"z{minZ + z}: ");
                            sb.AppendLine(new string(result.Rows[z]));
                        }

                        sb.AppendLine();
                        sb.AppendLine("## 图例");
                        sb.AppendLine(SymbolDictionary.GetLegendString(result.UsedSymbols));

                        return ToolResult.Success(sb.ToString().TrimEnd());
                    }
                    catch (Exception ex) { return ToolResult.Error($"地形查询失败: {ex.Message}"); }
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
