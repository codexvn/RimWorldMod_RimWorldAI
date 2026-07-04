using RimWorldMCP.MapRendering;

namespace RimWorldMCP.Compression
{
    public interface IChunkCompressor
    {
        string Name { get; }
        /// <summary>单值网格压缩（heatmap）</summary>
        string Compress(char[][] rows, (int x, int z) chunkIndex);
        /// <summary>多层网格压缩（get_tile_grid）</summary>
        string Compress(GridData grid, (int x, int z) chunkIndex);
    }
}
