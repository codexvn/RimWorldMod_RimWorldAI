using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorldAgent.Core.AgentRuntime;

namespace RimWorldAgent.Core.Skills
{
    public class SkillRegistry
    {
        private readonly Dictionary<string, SkillInfo> _skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly List<SkillDirectory> _skillDirs = new List<SkillDirectory>();

        public IReadOnlyList<SkillInfo> Skills => _skills.Values.ToList().AsReadOnly();
        public string BuiltinSkillsDir { get; private set; } = "";
        public string UserSkillsDir { get; private set; } = "";

        public void LoadFromDirectory(string skillsDir)
        {
            LoadFromDirectories(skillsDir, SkillStore.GetDefaultUserSkillsDir(skillsDir));
        }

        public void LoadFromDirectories(string builtinSkillsDir, string userSkillsDir)
        {
            BuiltinSkillsDir = Path.GetFullPath(builtinSkillsDir);
            UserSkillsDir = Path.GetFullPath(userSkillsDir);
            Directory.CreateDirectory(UserSkillsDir);

            _skillDirs.Clear();
            _skillDirs.Add(new SkillDirectory(BuiltinSkillsDir, "builtin"));
            _skillDirs.Add(new SkillDirectory(UserSkillsDir, "user"));

            Reload();
        }

        public void Reload()
        {
            _skills.Clear();
            foreach (var dir in _skillDirs)
                LoadDirectoryIntoRegistry(dir);

            CoreLog.Info($"[skills] 共加载 {_skills.Count} 个 Skill");
        }

        public SkillInfo? Get(string name)
        {
            _skills.TryGetValue(name, out var skill);
            return skill;
        }

        public List<SkillInfo> GetAll() => _skills.Values.OrderBy(s => s.Name).ToList();

        private void LoadDirectoryIntoRegistry(SkillDirectory dir)
        {
            var skillsDir = dir.Path;
            CoreLog.Info($"[skills] LoadDirectory 接收路径: {skillsDir} source={dir.Source}");
            if (!Directory.Exists(skillsDir))
            {
                CoreLog.Warn($"[skills] Skills 目录不存在: {skillsDir}");
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
                        skill.Source = dir.Source;
                        skill.IsOverride = dir.Source == "user" && _skills.ContainsKey(skill.Name);
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
                    CoreLog.Error($"[skills] 加载 Skill 异常 ({file}): {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

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

        private sealed class SkillDirectory
        {
            public SkillDirectory(string path, string source)
            {
                Path = path;
                Source = source;
            }

            public string Path { get; }
            public string Source { get; }
        }
    }
}
