using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Skills;

namespace RimWorldAgent.Core.AgentRuntime.Tools
{
    public class Tool_CreateSkill : IInternalTool
    {
        public string Name => "create_skill";
        public string Description => "创建或覆盖写入 Skills.d 中的领域知识 Skill。用于沉淀稳定、可复用的 RimWorld 操作经验，不用于临时计划。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "Skill 名称。小写字母、数字、短横线，例如 cold-snap-response。" },
                description = new { type = "string", description = "Skill 触发描述。说明何时应该使用该 Skill。" },
                content = new { type = "string", description = "Skill Markdown 正文，不需要包含 YAML frontmatter。" },
                tags = new { type = "array", items = new { type = "string" }, description = "可选，三级标签数组如 [\"概念/战斗/战术\",\"物品/装备/武器\"]，与 RimWorld Wiki 分类对齐。" },
                overwrite = new { type = "boolean", description = "是否覆盖已有同名 Skill 或创建同名内置 Skill 的 Skills.d 覆盖版本。默认 false。" }
            },
            required = new[] { "name", "description", "content" }
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
            var description = root.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "";
            var content = root.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
            var overwrite = root.TryGetProperty("overwrite", out var overwriteEl) && overwriteEl.ValueKind == JsonValueKind.True;
            var tags = ParseTags(root);

            var normalizedName = SkillStore.NormalizeName(name);
            var existing = registry.Get(normalizedName);
            if (existing != null && !overwrite)
            {
                var source = existing.Source == "user"
                    ? (existing.IsOverride ? "Skills.d 覆盖版本" : "Skills.d 自定义版本")
                    : "内置 Skills 版本";
                return Done($"Skill 已存在: {normalizedName} ({source})。如需覆盖，请设置 overwrite=true。");
            }

            var saveResult = store.SaveUserSkill(normalizedName, description, content, overwrite: true, tags);
            if (!saveResult.Success)
                return Done(saveResult.Message);

            registry.Reload();
            InternalToolRegistry.UpdateSkillsDesc();

            var action = existing == null ? "创建" : "覆盖";
            var mode = $"已{action}";
            var tagsStr = tags != null && tags.Count > 0 ? $", tags=[{string.Join(",", tags)}]" : "";
            var toolResult = $"{mode} Skill: {normalizedName}{tagsStr}\n路径: {saveResult.Path}\n\n已热加载，可立即调用 active_skill(name=\"{normalizedName}\") 使用。";
            ToolDispatcher.EnqueueSystemReminderSuffix($"Skill 已{action}: {normalizedName}。需要时调用 get_skills 查看最新列表，用 active_skill(name=\"{normalizedName}\") 激活最新内容。");

            return Done(toolResult);
        }

        private static Task<(string result, bool exit)> Done(string result) => Task.FromResult((result, false));

        private static List<string>? ParseTags(JsonElement root)
        {
            if (!root.TryGetProperty("tags", out var tagsEl)) return null;
            if (tagsEl.ValueKind != JsonValueKind.Array) return null;
            var tags = new List<string>();
            foreach (var item in tagsEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    tags.Add(item.GetString()!);
            return tags.Count > 0 ? tags : null;
        }
    }
}
