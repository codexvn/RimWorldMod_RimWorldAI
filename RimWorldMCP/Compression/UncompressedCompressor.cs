using System.Text;

namespace RimWorldMCP.Compression
{
    public class UncompressedCompressor : IChunkCompressor
    {
        public string Name => "未压缩";

        public string Compress(char[][] rows, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < rows.Length; r++)
            {
                sb.Append('L');
                sb.Append(r.ToString("D2"));
                sb.Append('=');
                sb.Append(rows[r]);
                if (r < rows.Length - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
