using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Data;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_ReadMemory : IInternalTool
    {
        public string Name => "read_memory";
        public string Description => "读取殖民地记忆文件，查看历史经验和已知信息。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                section = new { type = "string", description = "可选，只读取指定章节标题（如'记忆'、'殖民者'、'经验'），不传则返回全文" }
            }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var file = GetMemoryPath();
            if (!File.Exists(file))
                return Task.FromResult(("记忆文件不存在，殖民地刚开局尚无记录。", false));

            var content = File.ReadAllText(file);

            var section = args?.TryGetProperty("section", out var s) == true ? s.GetString() : null;
            if (!string.IsNullOrEmpty(section))
            {
                var sectionContent = ExtractSection(content, section);
                content = sectionContent ?? $"未找到章节 '{section}'。";
            }

            if (content.Length > 8000)
                content = content.Substring(0, 8000) + "\n\n（内容过长，已截断。用 section 参数读取指定章节以获取完整内容。）";

            return Task.FromResult((content, false));
        }

        public static string GetMemoryPath() =>
            Path.Combine(SessionStore.ProjectPath, "MEMORY.md");

        private static string? ExtractSection(string content, string sectionName)
        {
            var lines = content.Split('\n');
            var start = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if ((t.StartsWith("## ") || t.StartsWith("### ")) && t.Contains(sectionName))
                {
                    start = i;
                    break;
                }
            }
            if (start < 0) return null;

            var end = lines.Length;
            for (var i = start + 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("## ") || t.StartsWith("# "))
                { end = i; break; }
            }

            return string.Join("\n", lines, start, end - start);
        }
    }
}
