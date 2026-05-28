using System.Text;

namespace RimWorldMCP.Compression
{
    /// <summary>
    /// RLE 压缩: 连续相同字符合并为 {字符}{十进制次数}。网格字符不含 0-9，十进制数字无二义性。
    /// </summary>
    public class RleCompressor : IChunkCompressor
    {
        public string Name => "RLE";

        public string Compress(char[][] rows, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < rows.Length; r++)
            {
                sb.Append('L');
                sb.Append(r.ToString("D2"));
                sb.Append('=');
                EncodeRleRow(rows[r], sb);
                if (r < rows.Length - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        internal static void EncodeRleRow(char[] row, StringBuilder sb)
        {
            if (row.Length == 0) return;

            char current = row[0];
            int count = 1;

            for (int i = 1; i < row.Length; i++)
            {
                if (row[i] == current)
                {
                    count++;
                }
                else
                {
                    AppendRun(sb, current, count);
                    current = row[i];
                    count = 1;
                }
            }
            AppendRun(sb, current, count);
        }

        private static void AppendRun(StringBuilder sb, char c, int count)
        {
            sb.Append(c);
            if (count > 1)
                sb.Append(count);
        }
    }
}
