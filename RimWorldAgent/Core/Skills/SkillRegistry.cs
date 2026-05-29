using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Skills
{
    public class SkillRegistry
    {
        private readonly Dictionary<string, SkillInfo> _skills = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<SkillInfo> Skills => _skills.Values.ToList().AsReadOnly();

        public void LoadFromDirectory(string skillsDir)
        {
            _skills.Clear();

            CoreLog.Info($"[skills] LoadFromDirectory 接收路径: {skillsDir}");
            if (!Directory.Exists(skillsDir))
            {
                CoreLog.Error($"[skills] Skills 目录不存在: {skillsDir}");
                return;
            }

            var files = Directory.GetFiles(skillsDir, "*.md");
            CoreLog.Info($"[skills] 在 {skillsDir} 中找到 {files.Length} 个 .md 文件");
            if (files.Length == 0)
            {
                CoreLog.Warn($"[skills] 目录存在但没有 .md 文件: {skillsDir}");
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    var skill = ParseSkillFile(file);
                    if (skill != null)
                    {
                        _skills[skill.Name] = skill;
                        CoreLog.Info($"[skills] 已加载 Skill: {skill.Name} ({file})");
                    }
                    else
                    {
                        CoreLog.Warn($"[skills] 解析失败: {file}");
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Error($"[skills] 加载 Skill 异常 ({file}): {ex.Message}");
                }
            }

            CoreLog.Info($"[skills] 共加载 {_skills.Count} 个 Skill");
        }

        public SkillInfo? Get(string name)
        {
            _skills.TryGetValue(name, out var skill);
            return skill;
        }

        public List<SkillInfo> GetAll() => _skills.Values.ToList();

        private static SkillInfo? ParseSkillFile(string filePath)
        {
            var text = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var lines = text.Split('\n');

            if (lines.Length < 4 || lines[0].Trim() != "---")
            {
                CoreLog.Warn($"[skills] 缺少 frontmatter: {filePath}");
                return null;
            }

            string? name = null;
            string? description = null;
            int contentStart = -1;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line == "---")
                {
                    contentStart = i + 1;
                    break;
                }

                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = line.Substring(0, colonIdx).Trim().ToLowerInvariant();
                    var value = line.Substring(colonIdx + 1).Trim();
                    switch (key)
                    {
                        case "name": name = value; break;
                        case "description": description = value; break;
                    }
                }
            }

            if (string.IsNullOrEmpty(name) || contentStart < 0)
            {
                CoreLog.Warn($"[skills] 缺少必要字段: {filePath}");
                return null;
            }

            var contentLines = lines.Skip(contentStart)
                .SkipWhile(l => string.IsNullOrWhiteSpace(l))
                .ToArray();
            var content = string.Join("\n", contentLines).Trim();

            return new SkillInfo
            {
                Name = name!,
                Description = description ?? name!,
                Content = content,
                FilePath = filePath
            };
        }

    }
}
