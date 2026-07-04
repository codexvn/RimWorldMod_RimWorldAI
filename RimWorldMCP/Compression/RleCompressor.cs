using System.Text;
using RimWorldMCP.MapRendering;

namespace RimWorldMCP.Compression
{
    /// <summary>
    /// RLE 压缩: 连续相同 token 合并为 {token}{十进制次数}。
    ///   char 管线: token = 单字符
    ///   CellData 管线: token = CellSerializer.Serialize(cell)（可能是 [..] 或 符号{..}）
    /// 语法字符 [ ] { } , 和数字 0-9 已 RESERVED，不会出现在符号中，故十进制次数无二义性。
    /// </summary>
    public class RleCompressor : IChunkCompressor
    {
        public string Name => "RLE";

        // ===== 单值网格 (heatmap) =====

        public string Compress(char[][] rows, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < rows.Length; r++)
            {
                AppendRowHeader(sb, r);
                EncodeRleRowChar(rows[r], sb);
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
                AppendRowHeader(sb, r);
                EncodeRleRowCell(grid, rows[r], sb);
                if (r < rows.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        // ===== RLE 编码 =====

        private static void AppendRowHeader(StringBuilder sb, int r)
        {
            sb.Append('L');
            sb.Append(r.ToString("D2"));
            sb.Append('=');
        }

        internal static void EncodeRleRowChar(char[] row, StringBuilder sb)
        {
            if (row.Length == 0) return;
            char current = row[0];
            int count = 1;
            for (int i = 1; i < row.Length; i++)
            {
                if (row[i] == current) count++;
                else
                {
                    AppendRun(sb, current.ToString(), count);
                    current = row[i];
                    count = 1;
                }
            }
            AppendRun(sb, current.ToString(), count);
        }

        internal static void EncodeRleRowCell(GridData grid, CellData[] row, StringBuilder sb)
        {
            if (row.Length == 0) return;
            string currentToken = grid.Serializer.Serialize(row[0]);
            int count = 1;
            for (int i = 1; i < row.Length; i++)
            {
                var token = grid.Serializer.Serialize(row[i]);
                if (token == currentToken) count++;
                else
                {
                    AppendRun(sb, currentToken, count);
                    currentToken = token;
                    count = 1;
                }
            }
            AppendRun(sb, currentToken, count);
        }

        private static void AppendRun(StringBuilder sb, string token, int count)
        {
            sb.Append(token);
            if (count > 1) sb.Append(count);
        }
    }
}
