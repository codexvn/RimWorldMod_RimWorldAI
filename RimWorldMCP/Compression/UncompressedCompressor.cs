using System.Text;
using RimWorldMCP.MapRendering;

namespace RimWorldMCP.Compression
{
    /// <summary>无压缩: 逐格原样输出，仅加行头。</summary>
    public class UncompressedCompressor : IChunkCompressor
    {
        public string Name => "未压缩";

        // ===== 单值网格 (heatmap) =====

        public string Compress(char[][] rows, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < rows.Length; r++)
            {
                sb.Append('L');
                sb.Append(r.ToString("D2"));
                sb.Append('=');
                sb.Append(rows[r]);
                if (r < rows.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        // ===== 多层网格 (get_tile_grid) =====

        public string Compress(GridData grid, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            var rows = grid.Rows;
            for (int r = 0; r < rows.Length; r++)
            {
                sb.Append('L');
                sb.Append(r.ToString("D2"));
                sb.Append('=');
                foreach (var cell in rows[r])
                    sb.Append(grid.Serializer.Serialize(cell));
                if (r < rows.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
