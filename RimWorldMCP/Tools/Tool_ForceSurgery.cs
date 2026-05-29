using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_ForceSurgery : ITool
    {
        public string Name => "force_surgery";
        public string Description => "强制为殖民者执行手术，绕过所有正常限制（技能、药物、麻醉、失败率、Bill 系统）。直接调用 ApplyOnPawn。需显式确认 bypass_checks=true。极度危险，仅限紧急情况或 AI 明确判断必须强制执行时使用。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 thingIDNumber" },
                recipe_defName = new { type = "string", description = "手术配方 DefName，如 InstallBionicArm" },
                body_part = new { type = "string", description = "目标身体部位标签，如 左臂。不传则自动选第一个兼容部位" },
                bypass_checks = new { type = "boolean", description = "必须设为 true 确认明知风险：绕过技能/药物/麻醉/失败率/致死检查" }
            },
            required = new[] { "colonist_id", "recipe_defName", "bypass_checks" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");
            if (!args.Value.TryGetProperty("recipe_defName", out var jRecipe))
                return ToolResult.Error("缺少必填参数: recipe_defName");
            if (!args.Value.TryGetProperty("bypass_checks", out var jBypass)
                || jBypass.ValueKind != JsonValueKind.True)
                return ToolResult.Error("bypass_checks 必须设为 true 才能使用强制手术。此操作绕过所有安全检查，可能导致殖民者死亡。");

            var recipeDefName = jRecipe.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(recipeDefName))
                return ToolResult.Error("recipe_defName 不能为空");

            var bodyPartFilter = "";
            if (args.Value.TryGetProperty("body_part", out var jPart))
                bodyPartFilter = jPart.GetString() ?? "";

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                    .FirstOrDefault(c => c.thingIDNumber == colonistId);
                if (pawn == null)
                    return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                var recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
                if (recipe == null)
                    return ToolResult.Error($"找不到手术配方: {recipeDefName}");
                if (!recipe.IsSurgery)
                    return ToolResult.Error($"{recipe.label} 不是手术配方");

                BodyPartRecord? targetPart = null;
                if (recipe.targetsBodyPart)
                {
                    var parts = recipe.Worker.GetPartsToApplyOn(pawn, recipe).ToList();
                    if (parts.Count == 0)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 没有兼容 {recipe.label} 的身体部位。");

                    if (!string.IsNullOrEmpty(bodyPartFilter))
                    {
                        targetPart = parts.FirstOrDefault(p =>
                            p.Label != null &&
                            p.Label.IndexOf(bodyPartFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (targetPart == null)
                        {
                            var labels = string.Join(", ", parts.Select(p => p.Label).Take(10));
                            return ToolResult.Error($"找不到部位 '{bodyPartFilter}'。可用: {labels}");
                        }
                    }
                    else
                    {
                        targetPart = parts.First();
                    }
                }

                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"## 强制执行手术: {recipe.label}");

                    var workerType = recipe.workerClass?.Name ?? "RecipeWorker";
                    sb.AppendLine($"- 殖民者: {pawn.Name.ToStringShort}");
                    sb.AppendLine($"- 配方: {recipe.label} (`{recipe.defName}`)");
                    sb.AppendLine($"- Worker: {workerType}");

                    if (targetPart != null)
                        sb.AppendLine($"- 部位: {targetPart.Label}");

                    sb.AppendLine($"- 风险: 绕过所有安全检查（可能致死/致残）");

                    // 直接调用 ApplyOnPawn，绕过 Bill 系统
                    recipe.Worker.ApplyOnPawn(pawn, targetPart, null, new System.Collections.Generic.List<Thing>(), null);

                    sb.AppendLine();
                    if (recipe.addsHediff != null)
                        sb.AppendLine($"已应用: {recipe.addsHediff.label}");
                    if (recipe.removesHediff != null)
                        sb.AppendLine($"已移除: {recipe.removesHediff.label}");
                    sb.AppendLine("手术已强制执行。请立即检查殖民者状态。");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"强制手术失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            if (!args.Value.TryGetProperty("colonist_id", out var jC) || !jC.TryGetInt32(out var id)) return null;
            var pawn = CameraHelper.FindPawnById(map, id);
            if (pawn == null) return null;
            return (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
        }
    }
}
