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
    public class Tool_GetTileGrid : ITool
    {
        public string Name => "get_tile_grid";
        public string Description => "获取指定范围的文本化网格地图。返回字符网格，用不同符号标注地形、建筑、物品。用于 LLM 理解地图空间布局。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                min_x = new { type = "integer", description = "网格 X 范围最小值（水平轴）" },
                min_y = new { type = "integer", description = "网格 Y 范围最小值（垂直轴，即 IntVec3.z）" },
                max_x = new { type = "integer", description = "网格 X 范围最大值" },
                max_y = new { type = "integer", description = "网格 Y 范围最大值" }
            },
            required = new[] { "min_x", "min_y", "max_x", "max_y" }
        });

        // 完整图例映射表（动态输出时只显示实际用到的符号）
        private static readonly Dictionary<char, string> LegendMap = new()
        {
            // 建筑
            ['#'] = "墙",
            ['D'] = "门",
            ['B'] = "建筑",
            ['⊞'] = "工作台",
            ['◻'] = "床",
            ['☈'] = "炮塔",
            ['∎'] = "蓝图/框架",
            // 物品
            ['↑'] = "武器",
            ['▢'] = "衣物/护甲",
            ['+'] = "药品",
            ['!'] = "成瘾品",
            ['%'] = "食物",
            ['◇'] = "零件",
            ['●'] = "原材料",
            ['☠'] = "尸体",
            ['○'] = "物品",
            // 植物与区域
            [';'] = "作物",
            ['♣'] = "树",
            ['='] = "种植区",
            ['S'] = "储存区",
            // 地形
            ['~'] = "水面",
            ['≈'] = "泥地",
            ['·'] = "沙地",
            ['.'] = "土地",
            [':'] = "沃土",
            [','] = "砾石",
            ['?'] = "未知"
        };

        // 物品属性匹配规则（按优先级，首次命中即返回）
        private static readonly List<(Func<ThingDef, bool> Match, char Symbol)> ItemRules = new()
        {
            (d => d.IsWeapon || d.IsRangedWeapon || d.IsMeleeWeapon, '↑'),
            (d => d.IsApparel,                                    '▢'),
            (d => d.IsMedicine,                                   '+'),
            (d => d.IsDrug,                                        '!'),
            (d => d.IsNutritionGivingIngestible,                   '%'),
            (d => d.defName.Contains("Component"),                 '◇'),
            (d => d.IsStuff,                                       '●'),
        };

        // 常见物品 defName → 符号精确映射（优先于属性规则）
        private static readonly Dictionary<string, char> KnownItemIcons = new()
        {
            ["MealSimple"] = '%', ["MealFine"] = '%', ["MealLavish"] = '%',
            ["MealSurvivalPack"] = '%', ["MealNutrientPaste"] = '%',
            ["MedicineHerbal"] = '+', ["MedicineIndustrial"] = '+', ["MedicineUltratech"] = '+',
            ["ComponentIndustrial"] = '◇', ["ComponentSpacer"] = '◆',
            ["Steel"] = '●', ["WoodLog"] = '●', ["Plasteel"] = '●',
            ["Silver"] = '●', ["Gold"] = '●', ["Uranium"] = '●',
            ["Chemfuel"] = '●', ["Cloth"] = '●', ["Synthread"] = '●',
            ["Hyperweave"] = '●', ["DevilstrandCloth"] = '●',
            ["Corpse"] = '☠',
        };

        private static char GetItemIcon(Thing item)
        {
            var def = item.def;

            // 尸体特殊处理
            if (item is Corpse) return '☠';

            // 精确 defName 匹配
            if (KnownItemIcons.TryGetValue(def.defName, out var icon))
                return icon;

            // 属性规则匹配
            foreach (var (match, symbol) in ItemRules)
                if (match(def)) return symbol;

            // Fallback: 基于 defName 的确定性单字符（小写字母 a-z）
            return (char)('a' + Math.Abs(def.defName.GetHashCode()) % 26);
        }

        private static char GetBuildingIcon(Building b)
        {
            var def = b.def;
            if (def.altitudeLayer == AltitudeLayer.DoorMoveable) return 'D';
            if (b is Building_Turret) return '☈';
            if (b is Building_WorkTable) return '⊞';
            if (b is Building_Bed) return '◻';
            // 墙体 (altitudeLayer 低于 Building)
            return def.altitudeLayer >= AltitudeLayer.Building ? 'B' : '#';
        }

        private static char GetTerrainIcon(TerrainDef terrain)
        {
            var dn = terrain.defName;
            if (dn.Contains("Water") || dn.Contains("Marsh")) return '~';
            if (dn.Contains("Mud")) return '≈';
            if (dn.Contains("Sand")) return '·';
            if (dn.Contains("Rich")) return ':';
            if (dn.Contains("Soil")) return '.';
            if (dn.Contains("Gravel")) return ',';
            return '.';
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("min_x", out var jMinX) || !jMinX.TryGetInt32(out var minX))
                return ToolResult.Error("缺少 min_x");
            if (!args.Value.TryGetProperty("min_y", out var jMinY) || !jMinY.TryGetInt32(out var minY))
                return ToolResult.Error("缺少 min_y");
            if (!args.Value.TryGetProperty("max_x", out var jMaxX) || !jMaxX.TryGetInt32(out var maxX))
                return ToolResult.Error("缺少 max_x");
            if (!args.Value.TryGetProperty("max_y", out var jMaxY) || !jMaxY.TryGetInt32(out var maxY))
                return ToolResult.Error("缺少 max_y");

            if (maxX - minX > 80 || maxY - minY > 80)
                return ToolResult.Error("网格范围不能超过 80x80");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    int mapW = map.Size.x, mapH = map.Size.z;
                    if (minX < 0 || minY < 0 || maxX >= mapW || maxY >= mapH)
                        return ToolResult.Error($"坐标超出地图边界 (0~{mapW - 1}, 0~{mapH - 1})");

                    int w = maxX - minX + 1;
                    int h = maxY - minY + 1;

                    var grid = new char[h][];
                    for (int i = 0; i < h; i++) grid[i] = new char[w];
                    var usedSymbols = new HashSet<char>();

                    for (int gy = minY; gy <= maxY; gy++)
                    {
                        for (int gx = minX; gx <= maxX; gx++)
                        {
                            var pos = new IntVec3(gx, 0, gy);
                            int row = gy - minY;
                            int col = gx - minX;
                            char ch;

                            var b = pos.GetEdifice(map);
                            if (b != null)
                            {
                                ch = GetBuildingIcon(b);
                                grid[row][col] = ch; usedSymbols.Add(ch); continue;
                            }

                            var things = pos.GetThingList(map);
                            var bp = things.FirstOrDefault(t => t is Blueprint || t is Frame);
                            if (bp != null)
                            { ch = '∎'; grid[row][col] = ch; usedSymbols.Add(ch); continue; }

                            var item = things.FirstOrDefault(t => t.def.category == ThingCategory.Item || t is Corpse);
                            if (item != null)
                            { ch = GetItemIcon(item); grid[row][col] = ch; usedSymbols.Add(ch); continue; }

                            var plant = pos.GetPlant(map);
                            if (plant != null)
                            { ch = plant.def.plant.IsTree ? '♣' : ';'; grid[row][col] = ch; usedSymbols.Add(ch); continue; }

                            var zone = map.zoneManager?.ZoneAt(pos);
                            if (zone is Zone_Growing)
                            { ch = '='; grid[row][col] = ch; usedSymbols.Add(ch); continue; }
                            if (zone is Zone_Stockpile)
                            { ch = 'S'; grid[row][col] = ch; usedSymbols.Add(ch); continue; }

                            var terrain = map.terrainGrid.TerrainAt(pos);
                            ch = terrain != null ? GetTerrainIcon(terrain) : '?';
                            grid[row][col] = ch; usedSymbols.Add(ch);
                        }
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 网格 ({minX},{minY}) ~ ({maxX},{maxY})  [{w}x{h}]");
                    sb.AppendLine();
                    for (int row = 0; row < h; row++)
                    {
                        for (int col = 0; col < w; col++)
                            sb.Append(grid[row][col]);
                        sb.AppendLine();
                    }

                    // 动态图例
                    var legendParts = new List<string>();
                    foreach (var ch in usedSymbols.OrderBy(c => c))
                    {
                        if (LegendMap.TryGetValue(ch, out var label))
                            legendParts.Add($"{ch}={label}");
                    }
                    if (legendParts.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine(string.Join("  ", legendParts));
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"网格生成失败: {ex.Message}");
                }
            });
        }
    }
}
