using System;
using System.Collections.Generic;
using System.Text;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 弹框控件记录 → ASCII 渲染。
    /// OnGUI 期间调用 Record* 方法记录控件（绝对屏幕坐标），
    /// ToAsciiString 时统一构建字符网格并输出。
    /// </summary>
    public class DialogCaptureBuffer
    {
        // ========== 记录条目 ==========

        public enum EntryType { WindowTitle, Label, Button, Separator }

        public struct Entry
        {
            public EntryType Type;
            public string Text;
            public float AbsX;
            public float AbsY;
            public float AbsWidth;
            public float AbsHeight;
            public int ButtonIndex; // 由 RecordButton 按序分配
        }

        private readonly List<Entry> _entries = new();
        private int _buttonCounter;

        // ========== 像素→字符估算参数 ==========

        // GameFont.Small 在 1080p 下约 7-8px/char，行高 ~16-18px
        public float PixelsPerCharX { get; set; } = 8f;
        public float PixelsPerCharY { get; set; } = 17f;

        // ========== 记录方法（OnGUI 期间由 Harmony 补丁调用）==========

        public void RecordWindowTitle(string title, float x, float y)
        {
            _entries.Add(new Entry
            {
                Type = EntryType.WindowTitle,
                Text = title,
                AbsX = x, AbsY = y
            });
        }

        public void RecordLabel(string text, float x, float y, float width)
        {
            if (string.IsNullOrEmpty(text)) return;
            _entries.Add(new Entry
            {
                Type = EntryType.Label,
                Text = text,
                AbsX = x, AbsY = y, AbsWidth = width
            });
        }

        public void RecordButton(string text, float x, float y, float width)
        {
            if (string.IsNullOrEmpty(text)) return;
            _entries.Add(new Entry
            {
                Type = EntryType.Button,
                Text = text,
                AbsX = x, AbsY = y, AbsWidth = width,
                ButtonIndex = _buttonCounter++
            });
        }

        public int ButtonCount => _buttonCounter;

        // ========== 输出 ==========

        public string ToAsciiString()
        {
            if (_entries.Count == 0) return "";

            // ---- 按 y 坐标排序并分行 ----
            var rows = GroupIntoRows();

            // ---- 计算列宽 ----
            int contentWidth = CalculateMaxWidth(rows);
            contentWidth = Math.Min(contentWidth, 100); // 最大 100 字符宽
            contentWidth = Math.Max(contentWidth, 20);  // 最小 20

            // ---- 构建输出 ----
            var sb = new StringBuilder();
            DrawTopBorder(sb, contentWidth);
            bool firstRow = true;

            foreach (var row in rows)
            {
                if (!firstRow && row.HasButton && row.PrevHadLabel)
                {
                    // 正文→按钮之间画分割线
                    DrawSeparator(sb, contentWidth);
                }
                firstRow = false;

                foreach (var line in row.Render(contentWidth, PixelsPerCharX))
                    DrawContentLine(sb, contentWidth, line);
            }

            DrawBottomBorder(sb, contentWidth);

            return sb.ToString();
        }

        // ========== 分行逻辑 ==========

        private List<RowGroup> GroupIntoRows()
        {
            var rows = new List<RowGroup>();
            const float rowMergeThreshold = 8f; // y 坐标在此范围内视为同一行

            foreach (var entry in _entries)
            {
                // 将 entry 定位到最近的 row
                RowGroup? target = null;
                foreach (var r in rows)
                {
                    if (Math.Abs(entry.AbsY - r.BaseY) < rowMergeThreshold)
                    {
                        target = r;
                        break;
                    }
                }

                if (target == null)
                {
                    target = new RowGroup { BaseY = entry.AbsY };
                    rows.Add(target);
                }

                target.Add(entry);
            }

            // 标记相邻行的语义关系（用于判断是否画分割线）
            for (int i = 1; i < rows.Count; i++)
            {
                rows[i].PrevHadLabel = rows[i - 1].HasLabel && !rows[i - 1].HasButton;
                rows[i - 1].NextHasButton = rows[i].HasButton && !rows[i].HasLabel;
            }

            return rows;
        }

        private int CalculateMaxWidth(List<RowGroup> rows)
        {
            int max = 0;
            foreach (var row in rows)
            {
                int w = CjkWidth(row.FullText);
                if (w > max) max = w;
            }
            return max + 6; // 左右各 2 格 padding + 2 格框线
        }

        // ========== 绘制辅助 ==========

        private static void DrawTopBorder(StringBuilder sb, int w)
        {
            sb.Append('╔');
            sb.Append('═', w - 2);
            sb.AppendLine("╗");
        }

        private static void DrawSeparator(StringBuilder sb, int w)
        {
            sb.Append('╠');
            sb.Append('═', w - 2);
            sb.AppendLine("╣");
        }

        private static void DrawBottomBorder(StringBuilder sb, int w)
        {
            sb.Append('╚');
            sb.Append('═', w - 2);
            sb.AppendLine("╝");
        }

        private static void DrawContentLine(StringBuilder sb, int w, string line)
        {
            sb.Append("║ ");
            int written = 2; // 第一个空格在框线内

            for (int i = 0; i < line.Length && written < w - 2; i++)
            {
                char c = line[i];
                if (IsCjk(c))
                {
                    if (written + 2 <= w - 2)
                    {
                        sb.Append(c);
                        sb.Append(' '); // CJK 占 2 列
                        written += 2;
                    }
                }
                else
                {
                    sb.Append(c);
                    written++;
                }
            }

            // 填充空白到右边界
            while (written < w - 1)
            {
                sb.Append(' ');
                written++;
            }
            sb.AppendLine("║");
        }

        // ========== CJK ==========

        public static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified
                || (c >= 0x3400 && c <= 0x4DBF)    // Extension A
                || (c >= 0xF900 && c <= 0xFAFF)    // Compatibility
                || (c >= 0x3000 && c <= 0x303F)    // Symbols
                || (c >= 0xFF00 && c <= 0xFFEF)    // Fullwidth
                || (c >= 0xAC00 && c <= 0xD7AF);   // Hangul
        }

        public static int CjkWidth(string s)
        {
            int w = 0;
            foreach (char c in s)
                w += IsCjk(c) ? 2 : 1;
            return w;
        }

        // ========== RowGroup 内部类 ==========

        private class RowGroup
        {
            public float BaseY;
            public readonly List<Entry> Entries = new();
            public bool HasLabel => Entries.Exists(e => e.Type == EntryType.Label);
            public bool HasButton => Entries.Exists(e => e.Type == EntryType.Button);
            public bool PrevHadLabel;
            public bool NextHasButton;

            public string FullText
            {
                get
                {
                    var sb = new StringBuilder();
                    foreach (var e in Entries)
                    {
                        if (sb.Length > 0) sb.Append("  ");
                        switch (e.Type)
                        {
                            case EntryType.WindowTitle:
                                sb.Append(e.Text);
                                break;
                            case EntryType.Label:
                                sb.Append(e.Text);
                                break;
                            case EntryType.Button:
                                sb.Append($"[{e.ButtonIndex}] {e.Text}");
                                break;
                        }
                    }
                    return sb.ToString();
                }
            }

            public void Add(Entry e) => Entries.Add(e);

            /// <summary>渲染为一行或多行文本（支持 Label 自动换行）</summary>
            public List<string> Render(int totalWidth, float pxPerChar)
            {
                var lines = new List<string>();
                int innerW = totalWidth - 4; // 减去框线 + 左右 padding

                // 简单策略：标题居中，button 行原样，label 换行
                foreach (var entry in Entries)
                {
                    switch (entry.Type)
                    {
                        case EntryType.WindowTitle:
                            // 居中
                            string title = entry.Text;
                            int tw = CjkWidth(title);
                            int pad = Math.Max(0, (innerW - tw) / 2);
                            lines.Add(new string(' ', pad) + title);
                            break;

                        case EntryType.Label:
                            lines.AddRange(WrapText(entry.Text, innerW));
                            break;

                        case EntryType.Button:
                            // 最后一个 button 行，拼上已有的 buttons
                            if (lines.Count > 0 && Entries.Exists(e => e.Type == EntryType.Label))
                                lines.Add(""); // 空行
                            lines.Add(BuildButtonLine(innerW));
                            break;
                    }
                }
                return lines;
            }

            private string BuildButtonLine(int innerW)
            {
                var parts = new List<string>();
                foreach (var e in Entries)
                {
                    if (e.Type == EntryType.Button)
                        parts.Add($"[{e.ButtonIndex}] {e.Text}");
                }
                return string.Join("    ", parts);
            }

            private static List<string> WrapText(string text, int maxWidth)
            {
                var lines = new List<string>();
                var current = new StringBuilder();
                int currentW = 0;

                foreach (char c in text)
                {
                    if (c == '\n')
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        currentW = 0;
                        continue;
                    }

                    int cw = IsCjk(c) ? 2 : 1;
                    if (currentW + cw > maxWidth)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                        currentW = 0;
                    }
                    current.Append(c);
                    currentW += cw;
                }

                if (current.Length > 0)
                    lines.Add(current.ToString());

                return lines.Count > 0 ? lines : new List<string> { "" };
            }
        }
    }
}
