using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 各网格工具的单元格→字符映射函数。从原有工具中抽取，统一查 SymbolDictionary。
    /// 渲染优先级（get_tile_grid）: 迷雾 > 建筑 > 蓝图/框架 > 物品 > 植物 > 区域 > 地形
    /// </summary>
    public static class CellCharProviders
    {
        /// <summary>综合瓦片格 (get_tile_grid): 多层覆盖优先级</summary>
        public static (char symbol, string? category) ForTileGrid(IntVec3 pos, Map map)
        {
            // 0. 迷雾
            if (pos.Fogged(map))
                return ('█', "迷雾");

            // 1. 建筑 (edifice)
            var edifice = pos.GetEdifice(map);
            if (edifice != null)
            {
                // 蓝图/框架
                var thingsAtPos = pos.GetThingList(map);
                if (thingsAtPos != null)
                {
                    var blueprint = thingsAtPos.FirstOrDefault(t => t is Blueprint || t is Frame);
                    if (blueprint != null)
                        return ('∎', "蓝图/框架");
                }

                // 用 SymbolDictionary 查 def
                char c = SymbolDictionary.GetChar(edifice.def);
                return (c, "建筑");
            }

            // 2. 蓝图/框架（在地上）
            {
                var thingsAtPos = pos.GetThingList(map);
                if (thingsAtPos != null)
                {
                    var blueprint = thingsAtPos.FirstOrDefault(t => t is Blueprint || t is Frame);
                    if (blueprint != null)
                        return ('∎', "蓝图/框架");
                }
            }

            // 3. 物品/尸体
            {
                var things = pos.GetThingList(map);
                if (things != null)
                {
                    var item = things.FirstOrDefault(t =>
                        t.def.category == ThingCategory.Item || t is Corpse);
                    if (item != null)
                    {
                        char c = SymbolDictionary.GetChar(item.def);
                        return (c, "物品");
                    }
                }
            }

            // 4. 植物
            var plant = pos.GetPlant(map);
            if (plant != null)
            {
                char c = SymbolDictionary.GetChar(plant.def);
                return (c, "植物");
            }

            // 5. 区域 (Zone)
            var zone = map.zoneManager?.ZoneAt(pos);
            if (zone != null)
                return (zone is Zone_Growing ? '=' : 'S', "区域");

            // 6. 地形 (fallback)
            {
                var terrain = map.terrainGrid.TerrainAt(pos);
                if (terrain != null)
                {
                    char c = SymbolDictionary.GetChar(terrain);
                    return (c, "地形");
                }
            }

            return ('?', "未知");
        }

        /// <summary>纯地形格 (terrain_grid)</summary>
        public static (char symbol, string? category) ForTerrain(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map))
                return ('?', "迷雾");
            var terrain = map.terrainGrid.TerrainAt(pos);
            if (terrain == null) return ('?', "未知");
            return (SymbolDictionary.GetChar(terrain), "地形");
        }

        /// <summary>肥沃度格 (fertility_grid)，符号固定不查字典</summary>
        public static (char symbol, string? category) ForFertility(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map))
                return ('?', "迷雾");
            float f = map.fertilityGrid.FertilityAt(pos) * 100f;
            char c = f >= 140 ? '▓' : f >= 100 ? '▒' : f >= 70 ? '░' : '·';
            return (c, "肥沃度");
        }

        /// <summary>温度格 (temperature_grid)，符号固定不查字典</summary>
        public static (char symbol, string? category) ForTemperature(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map))
                return ('?', "迷雾");
            float t = GenTemperature.GetTemperatureForCell(pos, map);
            char c = t < -20 ? '█' : t < 0 ? '▓' : t < 10 ? '░' : t < 21 ? '.' : t < 35 ? '○' : t < 60 ? '◎' : '●';
            return (c, "温度");
        }

        /// <summary>污染格 (pollution_grid)，符号固定不查字典</summary>
        public static (char symbol, string? category) ForPollution(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map))
                return ('?', "迷雾");
            return map.pollutionGrid.IsPolluted(pos) ? ('P', "污染") : ('.', "干净");
        }
    }
}
