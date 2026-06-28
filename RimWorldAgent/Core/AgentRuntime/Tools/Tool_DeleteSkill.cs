using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Skills;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_DeleteSkill : IInternalTool
    {
        public string Name => "delete_skill";
        public string Description => "删除 Skills.d 中用户创建的 Skill。内置 Skill 不可删除。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "要删除的 Skill 名称" }
            },
            required = new[] { "name" }
        });

        public Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var registry = InternalToolRegistry.SkillRegistry;
            var store = InternalToolRegistry.SkillStore;
            if (registry == null || store == null)
                return Done("Skill 注册表未初始化。");
            if (args == null)
                return Done("参数不能为空。");

            var root = args.Value;
            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                return Done("缺少 name 参数。");

            var normalizedName = SkillStore.NormalizeName(name);
            var existing = registry.Get(normalizedName);
            if (existing == null)
                return Done($"Skill 不存在: {normalizedName}");
            if (existing.Source == "builtin" && !existing.IsOverride)
                return Done($"不允许删除内置 Skill: {normalizedName}。只能删除 Skills.d 中用户创建的 Skill。");

            var result = store.DeleteUserSkill(normalizedName);
            if (!result.Success)
                return Done(result.Message);

            registry.Reload();
            InternalToolRegistry.UpdateSkillsDesc();
            ToolDispatcher.EnqueueSystemReminderSuffix($"Skill 已删除: {normalizedName}。后续不要再调用该 Skill；如需确认可用列表，调用 get_skills。");

            return Done($"已删除 Skill: {normalizedName}。");
        }

        private static Task<(string result, bool exit)> Done(string result) => Task.FromResult((result, false));
    }
}
