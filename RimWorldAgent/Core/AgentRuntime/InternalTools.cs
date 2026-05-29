using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Skills;
using SimpleMspServer.Mcp;

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

    public class InternalToolRegistry : IToolProvider
    {
        public static InternalToolRegistry Instance { get; } = new InternalToolRegistry();

        private readonly Dictionary<string, InternalTool> _tools = new();
        private SkillRegistry? _skillRegistry;

        string IToolProvider.ProviderName => "AgentInternal";

        private InternalToolRegistry()
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
                Handler = args =>
                {
                    var summary = "";
                    if (args != null && args.Value.TryGetProperty("summary", out var s))
                        summary = s.GetString() ?? "";
                    return Task.FromResult((string.IsNullOrEmpty(summary) ? "战斗指挥官角色已退出。" : $"战斗指挥官已退出。\n总结: {summary}", true));
                }
            });
        }

        /// <summary>初始化 Skill 注册表，由 Loader 在启动时调用</summary>
        public void LoadSkills(string skillsDir)
        {
            _skillRegistry = new SkillRegistry();
            _skillRegistry.LoadFromDirectory(skillsDir);
        }

        /// <summary>在 SkillRegistry 加载后注册 skill 相关 Tool</summary>
        public void InitializeSkillTools()
        {
            Register(new InternalTool
            {
                Name = "get_skills",
                Description = "列出所有可用的领域知识 Skill",
                InputSchema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
                Handler = _ =>
                {
                    if (_skillRegistry == null) return Task.FromResult(("Skill 注册表未初始化。", false));
                    var sb = new StringBuilder();
                    sb.AppendLine("## 可用领域知识");
                    foreach (var s in _skillRegistry.GetAll())
                        sb.AppendLine($"- **{s.Name}**: {s.Description}");
                    return Task.FromResult((sb.ToString().TrimEnd(), false));
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
                Handler = args =>
                {
                    if (_skillRegistry == null) return Task.FromResult(("Skill 注册表未初始化。", false));
                    var name = args?.GetProperty("name").GetString() ?? "";
                    var skill = _skillRegistry.Get(name);
                    if (skill == null)
                    {
                        var names = string.Join(", ", _skillRegistry.GetAll().Select(s => s.Name));
                        return Task.FromResult(($"未知 Skill: {name}。可用: {names}", false));
                    }
                    return Task.FromResult(($"# {skill.Name}\n\n{skill.Content}", false));
                }
            });
        }

        public void Register(InternalTool tool) => _tools[tool.Name] = tool;

        public bool IsInternal(string name) => _tools.ContainsKey(name);

        public async Task<(string result, bool exit)> ExecuteInternalAsync(string name, JsonElement? args)
        {
            if (_tools.TryGetValue(name, out var tool) && tool.Handler != null)
                return await tool.Handler(args);
            return ($"内部工具 {name} 未注册", false);
        }

        public List<InternalTool> All => new(_tools.Values);

        // ===== IToolProvider =====

        List<ToolDefinition> IToolProvider.GetDefinitions()
        {
            return _tools.Values.Select(t => new ToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = JsonSerializer.SerializeToElement(t.InputSchema)
            }).ToList();
        }

        async Task<ToolCallResult> IToolProvider.ExecuteAsync(string name, JsonElement? args)
        {
            var (text, _) = await ExecuteInternalAsync(name, args);
            return new ToolCallResult
            {
                Content = new List<ContentItem> { new ContentItem { Type = "text", Text = text } }
            };
        }

        List<ResourceDefinition> IToolProvider.GetResources() => new List<ResourceDefinition>();
        string? IToolProvider.ReadResource(string uri) => null;
    }
}
