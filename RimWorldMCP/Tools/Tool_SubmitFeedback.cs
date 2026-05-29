using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldMCP.Tools
{
    public class Tool_SubmitFeedback : ITool
    {
        public string Name => "submit_feedback";
        public string Description => "提交反馈、问题报告或功能需求。内容将写入 mod 目录下的 feedback.md 文件，供开发者查阅。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                content = new { type = "string", description = "反馈内容" },
                category = new
                {
                    type = "string",
                    description = "反馈类型",
                    @enum = new[] { "建议", "问题", "需求" },
                    @default = "建议"
                }
            },
            required = new[] { "content" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return Task.FromResult(ToolResult.Error("缺少参数: content"));
            if (!args.Value.TryGetProperty("content", out var jContent))
                return Task.FromResult(ToolResult.Error("缺少必填参数: content"));

            var content = jContent.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                return Task.FromResult(ToolResult.Error("content 不能为空。"));

            var category = "建议";
            if (args.Value.TryGetProperty("category", out var jCat))
                category = jCat.GetString() ?? "建议";

            try
            {
                var modRoot = FindModRoot();
                if (modRoot == null)
                    return Task.FromResult(ToolResult.Error("无法定位 mod 根目录，反馈写入失败。"));
                var filePath = Path.Combine(modRoot, "feedback.md");

                var now = DateTime.Now;
                var timeStr = now.ToString("yyyy-MM-dd HH:mm");
                var entry = $"## {timeStr} — {category}\n\n{content.Trim()}\n\n---\n\n";

                if (File.Exists(filePath))
                {
                    var existing = File.ReadAllText(filePath);
                    File.WriteAllText(filePath, entry + existing);
                }
                else
                {
                    File.WriteAllText(filePath, "# AI 反馈记录\n\n" + entry);
                }

                return Task.FromResult(ToolResult.Success(
                    $"已提交{category}反馈到 {filePath}。感谢反馈！"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Error($"写入反馈失败: {ex.Message}"));
            }
        }

        /// <summary>定位 Mod 根目录。优先使用 RimWorld 官方 API，备选 Assembly.Location 向上两级。</summary>
        private static string? FindModRoot()
        {
            try
            {
                var rootDir = RimWorldMCPMod.Instance?.Content?.RootDir;
                if (!string.IsNullOrEmpty(rootDir))
                    return rootDir;
            }
            catch { }

            try
            {
                var asmPath = typeof(Tool_SubmitFeedback).Assembly.Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (asmDir != null)
                        return Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                }
            }
            catch { }

            return null;
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
