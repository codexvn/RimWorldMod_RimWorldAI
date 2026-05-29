using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Skills;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>Agent 内部 Tool，不经过 MCP，直接由 Agent 处理。</summary>
    public class InternalTool
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, object> InputSchema { get; set; } = new Dictionary<string, object>();
        /// <summary>返回 (resultText, shouldExitSession)</summary>
        public System.Func<JsonElement?, Task<(string result, bool exit)>>? Handler { get; set; }
    }

    public static class InternalToolRegistry
    {
        private static readonly Dictionary<string, InternalTool> _tools = new();

        private static SkillRegistry? _skillRegistry;

        /// <summary>初始化 Skill 注册表，由 Loader 在启动时调用</summary>
        public static void LoadSkills(string skillsDir)
        {
            _skillRegistry = new SkillRegistry();
            _skillRegistry.LoadFromDirectory(skillsDir);
        }

        static InternalToolRegistry()
        {
            Register(new InternalTool
            {
                Name = "exit_combat_role",
                Description = "退出战斗指挥官角色，恢复游戏速度。确保已处理完伤员和俘虏后再调用。",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["summary"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "战斗总结文本" }
                    }
                },
                Handler = async args =>
                {
                    var summary = "";
                    if (args != null && args.Value.TryGetProperty("summary", out var s))
                        summary = s.GetString() ?? "";
                    return (string.IsNullOrEmpty(summary) ? "战斗指挥官角色已退出。" : $"战斗指挥官已退出。\n总结: {summary}", true);
                }
            });

            // skill 工具由 InitializeSkillTools 动态注册
        }

        /// <summary>在 SkillRegistry 加载后注册 skill 相关 Tool</summary>
        public static void InitializeSkillTools()
        {
            Register(new InternalTool
            {
                Name = "get_skills",
                Description = "列出所有可用的领域知识 Skill",
                InputSchema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                Handler = async _ =>
                {
                    if (_skillRegistry == null) return ("Skill 注册表未初始化。", false);
                    var sb = new StringBuilder();
                    sb.AppendLine("## 可用领域知识");
                    foreach (var s in _skillRegistry.GetAll())
                        sb.AppendLine($"- **{s.Name}**: {s.Description}");
                    return (sb.ToString().TrimEnd(), false);
                }
            });

            Register(new InternalTool
            {
                Name = "active_skill",
                Description = "激活获取指定 Skill 的完整内容。传入 skill name。",
                InputSchema = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Skill 名称 (如 base-building, combat-preparation)" }
                    },
                    ["required"] = new[] { "name" }
                },
                Handler = async args =>
                {
                    if (_skillRegistry == null) return ("Skill 注册表未初始化。", false);
                    var name = args?.GetProperty("name").GetString() ?? "";
                    var skill = _skillRegistry.Get(name);
                    if (skill == null)
                    {
                        var names = string.Join(", ", _skillRegistry.GetAll().Select(s => s.Name));
                        return ($"未知 Skill: {name}。可用: {names}", false);
                    }
                    return ($"# {skill.Name}\n\n{skill.Content}", false);
                }
            });
        }

        public static void Register(InternalTool tool) => _tools[tool.Name] = tool;

        /// <summary>是否内部 Tool</summary>
        public static bool IsInternal(string name) => _tools.ContainsKey(name);

        /// <summary>执行内部 Tool，返回 (result, shouldExitSession)</summary>
        public static async Task<(string result, bool exit)> ExecuteAsync(string name, JsonElement? args)
        {
            if (_tools.TryGetValue(name, out var tool) && tool.Handler != null)
                return await tool.Handler(args);
            return ($"内部工具 {name} 未注册", false);
        }

        /// <summary>获取所有内部 Tool 定义（附加到 tools/list 结果中）</summary>
        public static List<InternalTool> All => new(_tools.Values);
    }
}
