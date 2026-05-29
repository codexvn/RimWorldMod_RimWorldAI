using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetResources : ITool
    {
        public string Name => "get_resources";
        public string Description => "获取殖民地当前资源库存详细报告，包括基础材料、食物、药品、装备、电力等。用于评估制造能力和资源瓶颈。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认50，最大100", @default = 50 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            int page = 1, pageSize = 50;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(100, jps.GetInt32()));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有激活的地图。");

                var resources = map.resourceCounter?.AllCountedAmounts;
                if (resources == null) return ToolResult.Error("无法获取资源计数。");

                var sb = new StringBuilder();
                sb.AppendLine("## 资源库存报告");

                // 构建并排序资源列表
                var resourceList = new List<(ThingDef def, int count)>();
                foreach (var kv in resources)
                {
                    if (kv.Value > 0)
                        resourceList.Add((kv.Key, kv.Value));
                }
                resourceList = resourceList.OrderByDescending(r => r.count).ThenBy(r => r.def.label).ToList();

                // 分页
                int total = resourceList.Count;
                var pagedResources = resourceList.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                // 分类资源
                var categories = new Dictionary<string, List<(string label, int count)>>()
                {
                    ["基础金属"] = new(),
                    ["石材"] = new(),
                    ["木材与植物材料"] = new(),
                    ["珍贵材料"] = new(),
                    ["原材料"] = new(),
                    ["加工材料"] = new(),
                    ["食物"] = new(),
                    ["医药"] = new(),
                    ["武器"] = new(),
                    ["衣物与护甲"] = new(),
                    ["装备与道具"] = new(),
                    ["其它"] = new(),
                };

                foreach (var (def, count) in pagedResources)
                {
                    string label = def.label;
                    string cat = CategorizeItem(def);
                    if (!categories.ContainsKey(cat))
                        cat = "其它";

                    categories[cat].Add((label, count));
                }

                foreach (var cat in categories)
                {
                    var items = cat.Value;
                    if (items.Count == 0) continue;

                    sb.AppendLine();
                    sb.AppendLine($"### {cat.Key}");
                    // 按数量降序排列
                    foreach (var item in items.OrderByDescending(i => i.count).ThenBy(i => i.label))
                    {
                        sb.AppendLine($"- {item.label}: {item.count}");
                    }
                }

                // 电力信息
                sb.AppendLine();
                sb.AppendLine("### 电力");
                try
                {
                    var powerNets = map.powerNetManager?.AllNetsListForReading;
                    if (powerNets != null && powerNets.Count > 0)
                    {
                        float totalGenerated = 0f, totalUsed = 0f, totalStored = 0f;
                        foreach (var net in powerNets)
                        {
                            totalStored += net.CurrentStoredEnergy();
                            foreach (var comp in net.powerComps)
                            {
                                if (comp.PowerOn)
                                {
                                    float rate = comp.EnergyOutputPerTick;
                                    if (rate > 0f)
                                        totalGenerated += rate;
                                    else
                                        totalUsed += -rate;
                                }
                            }
                        }
                        sb.AppendLine($"- 发电: {totalGenerated:N0}W");
                        sb.AppendLine($"- 用电: {totalUsed:N0}W");
                        sb.AppendLine($"- 储电: {totalStored:N0}Wd");
                        float surplus = totalGenerated - totalUsed;
                        string surplusLabel = surplus >= 0 ? $"剩余 {surplus:N0}W" : $"缺口 {Math.Abs(surplus):N0}W";
                        sb.AppendLine($"- 电力平衡: {surplusLabel}");
                    }
                }
                catch (Exception) { sb.AppendLine("- 无法读取电力信息"); }

                // 统计
                sb.AppendLine();
                sb.AppendLine($"---");
                sb.AppendLine($"*共 {total} 种物品有库存*");

                // 分页信息
                if (total > pageSize)
                {
                    int totalPages = (int)Math.Ceiling((double)total / pageSize);
                    sb.Append($"第 {page}/{totalPages} 页，共 {total} 条");
                    if (page < totalPages) sb.Append($" | page={page + 1} 下一页");
                    if (page > 1) sb.Append($" | page={page - 1} 上一页");
                    sb.AppendLine();
                }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string CategorizeItem(ThingDef def)
        {
            var dn = def.defName;

            // 金属
            if (dn == "Steel" || dn == "Plasteel" || dn == "Silver" || dn == "Gold" ||
                dn == "Uranium" || dn == "Jade")
                return "基础金属";

            // 石材块
            if (def.IsStuff && def.stuffProps?.categories?.Any(c => c.defName == "Stony") == true ||
                dn.Contains("Blocks") && (dn.Contains("Sandstone") || dn.Contains("Granite") || dn.Contains("Limestone") || dn.Contains("Slate") || dn.Contains("Marble")))
                return "石材";

            // 木材
            if (dn == "WoodLog" || (def.IsStuff && def.stuffProps?.categories?.Any(c => c.defName == "Woody") == true))
                return "木材与植物材料";

            // 珍贵材料
            if (dn == "ComponentIndustrial" || dn == "ComponentSpacer" || dn == "Chemfuel" || def.IsDrug)
                return "珍贵材料";

            // 原材料
            if (def.IsStuff || dn.Contains("Raw") || dn.Contains("Leather") || dn.Contains("Wool") ||
                dn.Contains("Cloth") || dn.Contains("Synthread") || dn.Contains("Hyperweave") || dn.Contains("Devilstrand"))
                return "原材料";

            // 加工材料
            if (dn.Contains("Component") || dn.Contains("Adv") && dn.Contains("Component"))
                return "加工材料";

            // 食物
            if (def.IsNutritionGivingIngestible || def.ingestible?.foodType != null ||
                def.IsIngestible && def.ingestible != null)
                return "食物";

            // 医药
            if (def.IsMedicine || dn.Contains("Medicine") || dn.Contains("Penoxycyline") ||
                dn.Contains("Luciferium") || dn.Contains("Healer"))
                return "医药";

            // 武器
            if (def.IsWeapon || def.IsRangedWeapon || def.IsMeleeWeapon)
                return "武器";

            // 衣物与护甲
            if (def.IsApparel)
                return "衣物与护甲";

            // 装备（建筑建材类等）
            if (def.thingClass != null && def.thingClass.Name.Contains("Building"))
                return "装备与道具";

            return "其它";
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
