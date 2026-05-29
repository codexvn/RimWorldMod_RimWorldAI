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
    public class Tool_ScheduleOperation : ITool
    {
        public string Name => "schedule_operation";
        public string Description => "为殖民者安排手术或医疗操作。注意：手术有失败风险，失败可能导致死亡。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists）" },
                recipe_defName = new { type = "string", description = "手术配方 DefName，如 InstallBionicArm" },
                body_part = new { type = "string", description = "目标身体部位标签（可选），如 左臂、右眼。不传则自动选择。" }
            },
            required = new[] { "colonist_id", "recipe_defName" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("colonist_id", out var jCid) || !jCid.TryGetInt32(out var colonistId))
                return ToolResult.Error("缺少必填参数: colonist_id");
            if (!args.Value.TryGetProperty("recipe_defName", out var jRecipe))
                return ToolResult.Error("缺少必填参数: recipe_defName");

            string recipeDefName = jRecipe.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(recipeDefName))
                return ToolResult.Error("recipe_defName 不能为空");

            string bodyPartFilter = "";
            if (args.Value.TryGetProperty("body_part", out var jPart))
                bodyPartFilter = jPart.GetString() ?? "";

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    // 查找殖民者
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn pawn = colonists.FirstOrDefault(c => c.thingIDNumber == colonistId);
                    if (pawn == null)
                        return ToolResult.Error($"找不到殖民者 ID={colonistId}");

                    // 查找 RecipeDef
                    RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeDefName);
                    if (recipe == null)
                        return ToolResult.Error($"找不到手术配方: {recipeDefName}。请确认 DefName 拼写正确。");

                    // 验证是否为手术
                    if (!recipe.IsSurgery)
                        return ToolResult.Error($"{recipe.label} ({recipeDefName}) 不是手术配方，不能通过安排手术执行。");

                    // 查找身体部位（如果指定）
                    BodyPartRecord? targetPart = null;
                    if (!string.IsNullOrEmpty(bodyPartFilter))
                    {
                        var hediffSet = pawn.health?.hediffSet;
                        if (hediffSet == null)
                            return ToolResult.Error("无法读取殖民者健康信息。");

                        var availableParts = hediffSet.GetNotMissingParts();
                        if (availableParts == null || !availableParts.Any())
                            return ToolResult.Error($"{pawn.Name.ToStringShort} 没有可用的身体部位。");

                        // 按标签模糊匹配
                        targetPart = availableParts.FirstOrDefault(p =>
                            p.Label != null &&
                            p.Label.IndexOf(bodyPartFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (targetPart == null)
                        {
                            // 列出可用部位帮助用户
                            var partLabels = availableParts.Select(p => p.Label).Take(10).ToArray();
                            return ToolResult.Error($"在 {pawn.Name.ToStringShort} 身上找不到匹配 '{bodyPartFilter}' 的身体部位。" +
                                                   $"可用部位示例: {string.Join(", ", partLabels)}");
                        }
                    }

                    // 获取 BillStack
                    BillStack billStack = pawn.BillStack;
                    if (billStack == null)
                        return ToolResult.Error($"无法访问 {pawn.Name.ToStringShort} 的手术队列。");

                    // 验证殖民者状态
                    if (pawn.Dead)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 已死亡，无法安排手术。");
                    if (pawn.health == null)
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 没有健康信息，无法安排手术。");

                    // 创建手术单据（必须先 AddBill 再设置 Part，Part setter 要求 billStack 不为 null）
                    Bill_Medical bill = new Bill_Medical(recipe, null);
                    billStack.AddBill(bill);
                    if (targetPart != null)
                    {
                        bill.Part = targetPart;
                    }

                    // 构建返回信息
                    var sb = new StringBuilder();
                    sb.AppendLine($"已为 {pawn.Name.ToStringShort} 安排手术: {recipe.label} ({recipeDefName})");

                    if (targetPart != null)
                        sb.AppendLine($"- 目标部位: {targetPart.Label}");

                    // 失败致死率
                    float deathChance = recipe.deathOnFailedSurgeryChance;
                    if (deathChance > 0f && deathChance < 1f)
                    {
                        string riskLevel = deathChance <= 0.02f ? "极低" :
                            deathChance <= 0.05f ? "低" :
                            deathChance <= 0.15f ? "中等" :
                            deathChance <= 0.30f ? "高" : "极高";
                        sb.AppendLine($"- 失败致死率: {deathChance * 100:F0}%（{riskLevel}风险）");

                        if (deathChance >= 0.15f)
                            sb.AppendLine($"- 警告：该手术有较高致死风险，请确保医生技能充足并在清洁环境中进行。");
                    }
                    else if (deathChance >= 1f)
                    {
                        sb.AppendLine($"- 失败致死率: 100%（失败必死）");
                    }
                    else
                    {
                        sb.AppendLine($"- 失败致死率: 无（失败不会直接致死）");
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"安排手术失败: {ex.Message}");
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
