using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Skills
{
    public class SkillStore
    {
        public SkillStore(string builtinSkillsDir, string userSkillsDir)
        {
            BuiltinSkillsDir = Path.GetFullPath(builtinSkillsDir);
            UserSkillsDir = Path.GetFullPath(userSkillsDir);
            Directory.CreateDirectory(UserSkillsDir);
        }

        public string BuiltinSkillsDir { get; }
        public string UserSkillsDir { get; }

        public static string GetDefaultUserSkillsDir(string builtinSkillsDir)
        {
            var full = Path.GetFullPath(builtinSkillsDir);
            var parent = Path.GetDirectoryName(full) ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(parent, "Skills.d");
        }

        public SkillWriteResult SaveUserSkill(string name, string description, string content, bool overwrite,
            List<string>? tags = null)
        {
            var normalized = NormalizeName(name);
            if (!IsValidName(normalized))
                return SkillWriteResult.Fail($"Skill 名称无效: {name}。只能使用小写字母、数字和短横线，长度 1-64。");
            if (string.IsNullOrWhiteSpace(description))
                return SkillWriteResult.Fail("Skill description 不能为空。");
            if (string.IsNullOrWhiteSpace(content))
                return SkillWriteResult.Fail("Skill content 不能为空。");

            Directory.CreateDirectory(UserSkillsDir);
            var path = GetUserSkillPath(normalized);
            if (File.Exists(path) && !overwrite)
                return SkillWriteResult.Fail($"Skill 已存在于 Skills.d: {normalized}。如需覆盖请设置 overwrite=true。");

            var text = BuildMarkdown(normalized, description.Trim(), StripFrontmatter(content).Trim(), tags);
            try
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
                CoreLog.Info($"[skills] 写入用户 Skill: {path}");
                return SkillWriteResult.Ok(path);
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[skills] 写入用户 Skill 失败: {ex.GetType().Name}: {ex.Message}");
                return SkillWriteResult.Fail($"写入失败: {ex.Message}");
            }
        }

        public SkillWriteResult DeleteUserSkill(string name)
        {
            var normalized = NormalizeName(name);
            if (!IsValidName(normalized))
                return SkillWriteResult.Fail($"Skill 名称无效: {name}");

            var path = GetUserSkillPath(normalized);
            if (!File.Exists(path))
                return SkillWriteResult.Fail($"Skills.d 中不存在 Skill: {normalized}");

            try
            {
                File.Delete(path);
                CoreLog.Info($"[skills] 删除用户 Skill: {path}");
                return SkillWriteResult.Ok(path);
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[skills] 删除用户 Skill 失败: {ex.GetType().Name}: {ex.Message}");
                return SkillWriteResult.Fail($"删除失败: {ex.Message}");
            }
        }

        public string GetUserSkillPath(string name)
        {
            var normalized = NormalizeName(name);
            var root = Path.GetFullPath(UserSkillsDir);
            var path = Path.GetFullPath(Path.Combine(root, normalized + ".md"));
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Skill 路径越界。");
            return path;
        }

        public static string NormalizeName(string name)
        {
            return (name ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')
                    continue;
                return false;
            }
            return !name.StartsWith("-") && !name.EndsWith("-") && !name.Contains("--");
        }

        private static string BuildMarkdown(string name, string description, string content, List<string>? tags = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {name}");
            sb.AppendLine($"description: {description.Replace("\r", " ").Replace("\n", " ").Trim()}");
            if (tags != null && tags.Count > 0)
                sb.AppendLine($"tags: {JsonSerializer.Serialize(tags)}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine(content.Trim());
            return sb.ToString();
        }

        private static string StripFrontmatter(string content)
        {
            var text = (content ?? "").Replace("\r\n", "\n");
            if (!text.StartsWith("---\n", StringComparison.Ordinal)) return content ?? "";

            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0) return content ?? "";
            var after = end + 4;
            if (after < text.Length && text[after] == '\n') after++;
            return text.Substring(after);
        }
    }

    public sealed class SkillWriteResult
    {
        private SkillWriteResult(bool success, string message, string path)
        {
            Success = success;
            Message = message;
            Path = path;
        }

        public bool Success { get; }
        public string Message { get; }
        public string Path { get; }

        public static SkillWriteResult Ok(string path) => new SkillWriteResult(true, "OK", path);
        public static SkillWriteResult Fail(string message) => new SkillWriteResult(false, message, "");
    }
}
