using System;
using System.IO;
using System.Text;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>
    /// 加载 Agent 的稳定约束 Prompt。它属于 ACP session 的 system prompt，
    /// 不应作为每一轮 session/prompt 的用户消息重复发送。
    /// </summary>
    internal static class AgentSystemPromptLoader
    {
        public static string Load(string promptPath, string projectPath, string? skillsDescPath = null)
        {
            var resolvedPath = promptPath;
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                var assemblyDir = Path.GetDirectoryName(typeof(AgentSystemPromptLoader).Assembly.Location);
                resolvedPath = Path.Combine(assemblyDir ?? AppDomain.CurrentDomain.BaseDirectory, "Prompt.md");
            }
            if (!File.Exists(resolvedPath))
                throw new FileNotFoundException("Agent system prompt file not found.", resolvedPath);

            var content = File.ReadAllText(resolvedPath, Encoding.UTF8);
            content = content.Replace("{projectPath}", projectPath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(skillsDescPath) && File.Exists(skillsDescPath))
                content = content.Replace("{skillsTable}", File.ReadAllText(skillsDescPath, Encoding.UTF8));
            else
                content = content.Replace("{skillsTable}", "(技能列表不可用，使用 get_skills 获取)");
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException("Agent system prompt file is empty.");

            return content.Trim();
        }
    }
}
