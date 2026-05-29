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
        public string Description => "获取指定 chunk 的污染网格（分块压缩）。需要 Biotech DLC。先用 list_chunks 获取 chunk_id 列表。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                chunk_id = new { type = "string", description = "Chunk ID，格式 \"X_Z\"" }
            },
            required = new[] { "chunk_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
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

                    int polluted = 0, total = 0;
                    var rows = new char[chunk.Height][];
                    for (int z = 0; z < chunk.Height; z++)
                    {
                        rows[z] = new char[chunk.Width];
                        for (int x = 0; x < chunk.Width; x++)
                        {
                            var pos = new IntVec3(chunk.MinX + x, 0, chunk.MinZ + z);
                            var (symbol, _) = CellCharProviders.ForPollution(pos, map);
                            rows[z][x] = symbol;
                            total++;
                            if (symbol == 'P') polluted++;
                        }
                    }

                    chunk.CompressedData = compressor.Compress(rows, (chunk.XIndex, chunk.ZIndex));

                    var sb = new StringBuilder();
                    sb.AppendLine($"## {Name}  Chunk({chunk.XIndex},{chunk.ZIndex})  世界({chunk.MinX},{chunk.MinZ})-({chunk.MaxX},{chunk.MaxZ})  [{chunk.Width}x{chunk.Height}]");
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

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
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
    }
}
