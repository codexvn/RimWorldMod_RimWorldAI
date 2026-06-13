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
    public class Tool_CreateBill : ITool, IRequiresAdvanceTick
    {
        public string Name => "create_production_bill";
        public string Description => "在指定工作台上创建制造（生产）单据。配方名称请先用 list_recipes 查询获取 defName。支持设置制造数量和重复模式。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                recipe_defName = new { type = "string", description = "配方的 defName，必须先用 list_recipes 确认配方存在。" },
                count = new { type = "integer", description = "制造数量，默认 1", @default = 1 },
                repeat_mode = new { type = "string", description = "重复模式", @enum = new[] { "RepeatCount", "Forever", "TargetCount" } },
                workbench_id = new { type = "integer", description = "目标工作台 ID (thingIDNumber)，不传则自动选择兼容的最佳工作台" }
            },
            required = new[] { "recipe_defName" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数: recipe_defName");
            if (!args.Value.TryGetProperty("recipe_defName", out var defNameProp))
                return ToolResult.Error("缺少必填参数: recipe_defName");

            var recipeDefName = defNameProp.GetString() ?? "";
            var count = 1;
            var repeatModeStr = "RepeatCount";
            if (args.Value.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv)) count = cv;
            if (args.Value.TryGetProperty("repeat_mode", out var rm))
                repeatModeStr = rm.GetString() ?? "RepeatCount";

            if (count < 1)
                return ToolResult.Error("制造数量必须大于 0。");

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    // 查找配方
                    var recipe = DefDatabase<RecipeDef>.GetNamed(recipeDefName, errorOnFail: false);
                    if (recipe == null)
                        return ToolResult.Error($"未知配方: {recipeDefName}。请先用 list_recipes 查询可用配方。");

                    // 检查配方是否可用（研究解锁、意识形态等）
                    if (!recipe.AvailableNow)
                        return ToolResult.Error($"配方 {recipe.label} ({recipeDefName}) 当前不可用。可能原因: 未研究解锁、或意识形态限制。");

                    var map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("当前没有可用地图。");

                    // 查找工作台
                    var workTables = map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().ToList();
                    if (workTables.Count == 0)
                        return ToolResult.Error("当前殖民地没有任何工作台。");

                    // 选择工作台：AI 指定 ID 或自动匹配兼容的
                    Building_WorkTable? targetTable = null;
                    if (args.Value.TryGetProperty("workbench_id", out var wbIdEl) && wbIdEl.TryGetInt32(out var wbId))
                    {
                        targetTable = workTables.FirstOrDefault(t => t.thingIDNumber == wbId);
                        if (targetTable == null)
                            return ToolResult.Error($"找不到 ID 为 {wbId} 的工作台。可用工作台: {string.Join(", ", workTables.Select(t => $"{t.def?.label}(ID:{t.thingIDNumber})"))}");
                    }
                    else
                    {
                        // 自动选择兼容工作台，跳过单据已满的
                        var compatible = workTables.Where(t => recipe.AvailableOnNow(t, null) && t.billStack.Count < 15).ToList();
                        targetTable = compatible.Count > 0 ? compatible[0] : workTables[0];
                    }

                    var tableLabel = targetTable.def?.label ?? targetTable.def?.defName ?? "工作台";

                    // 检查配方与工作台兼容性
                    if (!recipe.AvailableOnNow(targetTable, null))
                        return ToolResult.Error($"配方 {recipe.label} ({recipeDefName}) 无法在 {tableLabel} 上执行。可用工作台: {string.Join(", ", workTables.Where(t => recipe.AvailableOnNow(t, null)).Select(t => t.def?.label))}");

                    // 检查工作台单据数量上限
                    if (targetTable.billStack.Count >= 15)
                        return ToolResult.Error($"{tableLabel} 的单据已满（最多 15 个）。请先删除或暂停其他单据。");

                    // 检查原料库存
                    var missing = recipe.PotentiallyMissingIngredients(null, map).ToList();
                    if (missing.Count > 0)
                    {
                        var names = missing.Select(d => d.label).ToList();
                        return ToolResult.Error($"配方 {recipe.label} 缺少原料: {string.Join(", ", names)}");
                    }

                    // 创建单据（使用 MakeNewBill 自动选择正确的单据子类：UFT/Mech/Autonomous/标准）
                    var billBase = recipe.MakeNewBill(null);
                    var bill = (Bill_Production)billBase; // 所有 MakeNewBill 返回类型均继承自 Bill_Production
                    bill.targetCount = count;

                    switch (repeatModeStr)
                    {
                        case "Forever":
                            bill.repeatMode = BillRepeatModeDefOf.Forever;
                            break;
                        case "TargetCount":
                            bill.repeatMode = BillRepeatModeDefOf.TargetCount;
                            bill.targetCount = count;
                            break;
                        case "RepeatCount":
                        default:
                            bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
                            bill.repeatCount = count;
                            break;
                    }

                    targetTable.billStack.AddBill(billBase);

                    var billLabel = billBase.Label ?? recipeDefName;
                    var repeatText = repeatModeStr switch
                    {
                        "Forever" => "永久重复",
                        "TargetCount" => $"目标数量 {count} 件",
                        _ => $"重复 {count} 次"
                    };

                    var pos = targetTable.Position;
                    var sb = new StringBuilder();
                    sb.AppendLine($"已在 {tableLabel} (ID:{targetTable.thingIDNumber}) 创建制造单据:");
                    sb.AppendLine($"- 配方: {billLabel} ({recipeDefName})");
                    sb.AppendLine($"- 模式: {repeatText}");
                    sb.AppendLine($"- 位置: ({pos.x}, {pos.z})");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"创建制造单据失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
