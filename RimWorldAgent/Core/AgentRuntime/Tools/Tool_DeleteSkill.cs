using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.models;
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

        public async Task<(string result, bool exit)> ExecuteAsync(JsonElement? args)
        {
            var registry = InternalToolRegistry.SkillRegistry;
            var store = InternalToolRegistry.SkillStore;
            if (registry == null || store == null)
                return ("Skill 注册表未初始化。", false);
            if (args == null)
                return ("参数不能为空。", false);

            var root = args.Value;
            var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                return ("缺少 name 参数。", false);

            var normalizedName = SkillStore.NormalizeName(name);
            var existing = registry.Get(normalizedName);
            if (existing == null)
                return ($"Skill 不存在: {normalizedName}", false);
            if (existing.Source == "builtin" && !existing.IsOverride)
                return ($"不允许删除内置 Skill: {normalizedName}。只能删除 Skills.d 中用户创建的 Skill。", false);

            var result = store.DeleteUserSkill(normalizedName);
            if (!result.Success)
                return (result.Message, false);

            registry.Reload();
            InternalToolRegistry.UpdateSkillsDesc();

            // 中断当前会话 + 模拟用户输入，让 LLM 感知 Skill 已删除
            var ccbWs = AgentOrchestrator.CcbWs;
            if (AgentOrchestrator.IsRunning && ccbWs != null && ccbWs.IsReady)
            {
                try
                {
                    var userMsg = $"已删除 Skill: {normalizedName}";
                    await ccbWs.SendAbort();
                    UIMessageBus.PushUiMessage(UiMessage.User(userMsg));
                    await ccbWs.SendChat(ChatChannel.Bus, userMsg);
                    CoreLog.Info($"[delete_skill] 已中断会话并通知 LLM: {userMsg}");
                }
                catch (System.Exception ex)
                {
                    CoreLog.Warn($"[delete_skill] 中断会话失败: {ex.Message}，Skill 已删除但 LLM 未感知，将在下次会话生效");
                }
            }

            return ($"已删除 Skill: {normalizedName}。", false);
        }
    }
}
