using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_GetSkills : IInternalTool
    {
        public string Name => "get_skills";
        public string Description => "列出所有可用的领域知识 Skill";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var registry = InternalToolRegistry.SkillRegistry;
            if (registry == null) return Task.FromResult(("Skill 注册表未初始化。", false));
            var sb = new StringBuilder();
            sb.AppendLine("## 可用领域知识");
            foreach (var s in registry.GetAll())
                sb.AppendLine($"- **{s.Name}**: {s.Description}");
            return Task.FromResult((sb.ToString().TrimEnd(), false));
        }
    }
}
