using System.Collections.Generic;
using System.Text;
using RimWorldMCP.MapRendering;

namespace RimWorldMCP.Compression
{
    /// <summary>
    /// RowRef + RLE: 在 RLE 基础上，相同 RLE 字符串的行用 *L{refRow} 引用。
    /// 引用仅在同一 chunk 内有效，被引用行必须是该 chunk 内的完整 RLE 行。
    /// </summary>
    public class RowRefCompressor : IChunkCompressor
    {
        public string Name => "行引用+RLE";

        // ===== 单值网格 (heatmap) =====

        public string Compress(char[][] rows, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();

            var rleStrings = new string[rows.Length];
            var rleToFirstRow = new Dictionary<string, int>();
            for (int r = 0; r < rows.Length; r++)
            {
                var rowSb = new StringBuilder();
                RleCompressor.EncodeRleRowChar(rows[r], rowSb);
                rleStrings[r] = rowSb.ToString();
                if (!rleToFirstRow.ContainsKey(rleStrings[r]))
                    rleToFirstRow[rleStrings[r]] = r;
            }

            AppendRows(sb, rleStrings, rleToFirstRow);
            return sb.ToString();
        }

        // ===== 多层网格 (get_tile_grid) =====

        public string Compress(GridData grid, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            var rows = grid.Rows;

            var rleStrings = new string[rows.Length];
            var rleToFirstRow = new Dictionary<string, int>();
            for (int r = 0; r < rows.Length; r++)
            {
                var rowSb = new StringBuilder();
                RleCompressor.EncodeRleRowCell(grid, rows[r], rowSb);
                rleStrings[r] = rowSb.ToString();
                if (!rleToFirstRow.ContainsKey(rleStrings[r]))
                    rleToFirstRow[rleStrings[r]] = r;
            }

            AppendRows(sb, rleStrings, rleToFirstRow);
            return sb.ToString();
        }

        // 两管线共用: 按 rleStrings 输出，重复行用 *L{ref} 引用
        private static void AppendRows(StringBuilder sb, string[] rleStrings, Dictionary<string, int> rleToFirstRow)
        {
            for (int r = 0; r < rleStrings.Length; r++)
            {
                sb.Append('L');
                sb.Append(r.ToString("D2"));
                sb.Append('=');
                if (rleToFirstRow[rleStrings[r]] < r)
                {
                    sb.Append("*L");
                    sb.Append(rleToFirstRow[rleStrings[r]].ToString("D2"));
                }
                else
                {
                    sb.Append(rleStrings[r]);
                }
                if (r < rleStrings.Length - 1)
                    sb.Append('\n');
            }
        }
    }
}
