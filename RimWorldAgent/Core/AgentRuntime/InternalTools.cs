using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Skills;
using RimWorldAgent.Core.AgentRuntime.Tools;
using SimpleMspServer.Mcp;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>内部 Tool 数据类（保留用于动态注册场景）。</summary>
    public class InternalTool
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, object> InputSchema { get; set; } = new Dictionary<string, object>();
        public Func<JsonElement?, Task<(string result, bool exit)>>? Handler { get; set; }
    }

    public class InternalToolRegistry : IToolProvider
    {
        public static InternalToolRegistry Instance { get; } = new InternalToolRegistry();
        internal static SkillRegistry? SkillRegistry { get; private set; }

        /// <summary>内部工具请求退出会话时触发（exit=true）</summary>
        public static event Action? OnExitRequested;

        private readonly Dictionary<string, IInternalTool> _tools = new();

        string IToolProvider.ProviderName => "AgentInternal";

        private InternalToolRegistry()
        {
            Register(new Tool_EnterPlan());
            Register(new Tool_EnterAct());
            Register(new Tool_GetSkills());
            Register(new Tool_ActiveSkill());
            Register(new Tool_SetToolResultSuffix());
            CoreLog.Info($"[InternalToolRegistry] 注册 {_tools.Count} 个内部工具");
        }

        /// <summary>初始化 Skill 注册表，由 Loader 在启动时调用</summary>
        public void LoadSkills(string skillsDir)
        {
            SkillRegistry = new SkillRegistry();
            SkillRegistry.LoadFromDirectory(skillsDir);
        }

        public void Register(IInternalTool tool) => _tools[tool.Name] = tool;

        public bool IsInternal(string name) => _tools.ContainsKey(name);

        public async Task<(string result, bool exit)> ExecuteInternalAsync(string name, JsonElement? args)
        {
            if (_tools.TryGetValue(name, out var tool))
            {
                CoreLog.Debug($"[TOOL_CALL] InternalTool {name} args={FormatArgsForLog(args)}");
                var (result, exit) = await tool.ExecuteAsync(args);
                if (exit) OnExitRequested?.Invoke();
                return (result, exit);
            }
            return ($"内部工具 {name} 未注册", false);
        }

        public List<IInternalTool> All => new(_tools.Values);

        // ===== IToolProvider =====

        List<ToolDefinition> IToolProvider.GetDefinitions()
        {
            return _tools.Values.Select(t => new ToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema
            }).ToList();
        }

        async Task<ToolCallResult> IToolProvider.ExecuteAsync(string name, JsonElement? args)
        {
            try
            {
                var (text, _) = await ExecuteInternalAsync(name, args);
                return new ToolCallResult
                {
                    Content = new List<ContentItem> { new ContentItem { Type = "text", Text = text } }
                };
            }
            catch (Exception ex)
            {
                CoreLog.Error($"InternalTool {name} 异常: {ex.GetType().Name}: {ex.Message}");
                return new ToolCallResult
                {
                    IsError = true,
                    Content = new List<ContentItem> { new ContentItem { Type = "text", Text = $"工具 {name} 执行异常: {ex.Message}" } }
                };
            }
        }

        List<ResourceDefinition> IToolProvider.GetResources() => new List<ResourceDefinition>();
        string? IToolProvider.ReadResource(string uri) => null;

        private static string FormatArgsForLog(JsonElement? args)
        {
            if (args == null) return "null";
            try
            {
                return JsonSerializer.Serialize(args.Value, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch { return args.Value.GetRawText(); }
        }
    }
}
