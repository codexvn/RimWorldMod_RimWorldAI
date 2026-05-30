using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_ActiveSkill : IInternalTool
    {
        public string Name => "active_skill";
        public string Description => "激活获取指定 Skill 的完整内容。传入 skill name。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "Skill 名称 (如 base-building, combat-preparation)" }
            },
            required = new[] { "name" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var registry = InternalToolRegistry.SkillRegistry;
            if (registry == null) return Task.FromResult(("Skill 注册表未初始化。", false));
            if (args == null || !args.Value.TryGetProperty("name", out var nameEl) || string.IsNullOrWhiteSpace(nameEl.GetString()))
                return Task.FromResult(("Skill 名称不能为空。", false));
            var name = nameEl.GetString()!;
            var skill = registry.Get(name);
            if (skill == null)
            {
                var names = string.Join(", ", registry.GetAll().Select(s => s.Name));
                return Task.FromResult(($"未知 Skill: {name}。可用: {names}", false));
            }
            return Task.FromResult(($"# {skill.Name}\n\n{skill.Content}", false));
        }
    }
}
