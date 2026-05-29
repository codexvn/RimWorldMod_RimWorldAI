using System.Collections.Generic;
using System.Text;

namespace RimWorldMCP.Compression
{
    /// <summary>
    /// RowRef + RLE: 在 RLE 基础上，相同 RLE 字符串的行用 *L{refRow} 引用。
    /// 引用仅在同一 chunk 内有效，被引用行必须是该 chunk 内的完整 RLE 行。
    /// </summary>
    public class RowRefCompressor : IChunkCompressor
    {
        public string Name => "行引用+RLE";

        public string Compress(char[][] rows, (int x, int z) chunkIndex)
        {
            var sb = new StringBuilder();
            // 第一遍: 计算每行的 RLE 字符串
            var rleStrings = new string[rows.Length];
            var rleToFirstRow = new Dictionary<string, int>();

            for (int r = 0; r < rows.Length; r++)
            {
                var rowSb = new StringBuilder();
                RleCompressor.EncodeRleRow(rows[r], rowSb);
                rleStrings[r] = rowSb.ToString();

                if (!rleToFirstRow.ContainsKey(rleStrings[r]))
                    rleToFirstRow[rleStrings[r]] = r;
            }

            // 第二遍: 输出，重复行使用引用
            for (int r = 0; r < rows.Length; r++)
            {
                sb.Append('L');
                sb.Append(r.ToString("D2"));
                sb.Append('=');

                if (rleToFirstRow[rleStrings[r]] < r)
                {
                    // 引用首次出现的行
                    sb.Append("*L");
                    sb.Append(rleToFirstRow[rleStrings[r]].ToString("D2"));
                }
                else
                {
                    sb.Append(rleStrings[r]);
                }

                if (r < rows.Length - 1)
                    sb.Append('\n');
            }

            return sb.ToString();
        }
    }
}
