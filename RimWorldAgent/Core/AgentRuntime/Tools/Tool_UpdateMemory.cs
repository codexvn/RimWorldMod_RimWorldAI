using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_UpdateMemory : IInternalTool
    {
        public string Name => "update_memory";
        public string Description => "更新殖民地记忆文件 (CLAUDE.md)。可新增章节、替换章节、追加条目。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                section = new { type = "string", description = "目标章节标题（如'记忆'、'殖民者'、'经验'）" },
                action = new { type = "string", description = "操作类型: append（追加）, replace（替换整个章节）, new_section（新建章节）" },
                content = new { type = "string", description = "要写入的内容。append 时追加到该章节末尾；replace 时替换整个章节；new_section 时在文件末尾新增" },
                date_header = new { type = "string", description = "append 时可选，添加日期子标题（如'2026-05-31 - Day 5 夏季'）" }
            },
            required = new[] { "section", "action", "content" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(("参数缺失：需要 section, action, content。", false));

            var section = args.Value.GetProperty("section").GetString()!;
            var action = args.Value.GetProperty("action").GetString()!;
            var content = args.Value.GetProperty("content").GetString()!;
            var dateHeader = args.Value.TryGetProperty("date_header", out var dh) ? dh.GetString() : null;

            var file = Tool_ReadMemory.GetMemoryPath();
            var existing = File.Exists(file) ? File.ReadAllText(file) : "# RimWorld AI 会话记录\n";

            var result = action switch
            {
                "append" => AppendToSection(existing, section, content, dateHeader),
                "replace" => ReplaceSection(existing, section, content),
                "new_section" => existing.TrimEnd() + "\n\n" + content.Trim(),
                _ => throw new ArgumentException($"未知操作类型: {action}")
            };

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, result);
            return Task.FromResult(($"已更新记忆文件 '{section}' (操作: {action})。", false));
        }

        private static string AppendToSection(string existing, string section, string content, string? dateHeader)
        {
            var lines = new List<string>(existing.Split('\n'));
            var sectionIdx = FindSectionHeading(lines, section);
            if (sectionIdx < 0)
            {
                // 章节不存在，在末尾新建
                var sb = new StringBuilder(existing.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"## {section}");
                if (!string.IsNullOrEmpty(dateHeader))
                    sb.AppendLine($"### {dateHeader}");
                sb.Append(content.Trim());
                return sb.ToString();
            }

            // 找到章节末尾（下一个同级或更高级标题之前）
            var insertAfter = sectionIdx + (lines[sectionIdx].TrimStart().StartsWith("### ") ? 1 : 1);
            var headingLevel = lines[sectionIdx].TrimStart().TakeWhile(c => c == '#').Count();
            var insertAt = lines.Count;
            for (var i = sectionIdx + 1; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("#") && t.TakeWhile(c => c == '#').Count() <= headingLevel)
                { insertAt = i; break; }
            }

            var insertLines = new List<string>();
            if (!string.IsNullOrEmpty(dateHeader))
                insertLines.Add($"### {dateHeader}");
            insertLines.AddRange(content.Trim().Split('\n'));

            // 确保与下文有空行
            if (insertAt < lines.Count && !string.IsNullOrWhiteSpace(lines[insertAt]))
                insertLines.Add("");

            lines.InsertRange(insertAt, insertLines);
            return string.Join("\n", lines);
        }

        private static string ReplaceSection(string existing, string section, string content)
        {
            var lines = new List<string>(existing.Split('\n'));
            var sectionIdx = FindSectionHeading(lines, section);
            if (sectionIdx < 0)
            {
                // 不存在则新建
                var sb = new StringBuilder(existing.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine($"## {section}");
                sb.Append(content.Trim());
                return sb.ToString();
            }

            var headingLevel = lines[sectionIdx].TrimStart().TakeWhile(c => c == '#').Count();
            var endIdx = lines.Count;
            for (var i = sectionIdx + 1; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("#") && t.TakeWhile(c => c == '#').Count() <= headingLevel)
                { endIdx = i; break; }
            }

            var replacement = new List<string> { lines[sectionIdx] };
            replacement.AddRange(content.Trim().Split('\n'));
            if (endIdx < lines.Count && !string.IsNullOrWhiteSpace(lines[endIdx]))
                replacement.Add("");

            lines.RemoveRange(sectionIdx, endIdx - sectionIdx);
            lines.InsertRange(sectionIdx, replacement);
            return string.Join("\n", lines);
        }

        private static int FindSectionHeading(List<string> lines, string sectionName)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if ((t.StartsWith("## ") || t.StartsWith("### ")) && t.Contains(sectionName))
                    return i;
            }
            return -1;
        }
    }
}
