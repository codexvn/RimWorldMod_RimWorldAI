using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 各网格工具的单元格→字符映射函数。统一查 SymbolDictionary。
    /// 渲染优先级匹配游戏 AltitudeLayer（高 Y 优先）:
    ///   蓝图(9.5) > Pawn(8.4) > 物品(6.6) > 建筑(5.5) > 植物(4.0) > 区域(3.3) > 地形(0.7)
    /// </summary>
    public static class CellCharProviders
    {
        /// <summary>综合瓦片格 (get_tile_grid): 高 Y（近镜头）优先显示</summary>
        public static (char symbol, string? category) ForTileGrid(IntVec3 pos, Map map)
        {
            // 0. 迷雾
            if (pos.Fogged(map))
                return ('█', "迷雾");

            var thingsAtPos = pos.GetThingList(map);

            // 1. 蓝图/框架 — 游戏 AltitudeLayer.Blueprint(9.5), 最顶层
            if (thingsAtPos != null)
            {
                var blueprint = thingsAtPos.FirstOrDefault(t => t is Blueprint || t is Frame);
                if (blueprint != null)
                    return ('∎', "蓝图/框架");
            }

            // 2. 生物 (Pawn) — AltitudeLayer.Pawn(8.4), 在物品之上
            if (thingsAtPos != null)
            {
                var pawn = thingsAtPos.FirstOrDefault(t => t is Pawn);
                if (pawn != null)
                    return (SymbolDictionary.GetChar(pawn.def), "生物");
            }

            // 3. 物品/尸体 — AltitudeLayer.Item(6.6) / ItemImportant(6.9)
            if (thingsAtPos != null)
            {
                var item = thingsAtPos.FirstOrDefault(t =>
                    t.def.category == ThingCategory.Item || t is Corpse);
                if (item != null)
                    return (SymbolDictionary.GetChar(item.def), "物品");
            }

            // 4. 建筑 (edifice) — AltitudeLayer.Building(5.5)
            var edifice = pos.GetEdifice(map);
            if (edifice != null)
                return (SymbolDictionary.GetChar(edifice.def), "建筑");

            // 5. 植物 — AltitudeLayer.LowPlant(4.0)
            var plant = pos.GetPlant(map);
            if (plant != null)
                return (SymbolDictionary.GetChar(plant.def), "植物");

            // 6. 区域 (Zone) — AltitudeLayer.Zone(3.3)
            var zone = map.zoneManager?.ZoneAt(pos);
            if (zone != null)
                return (zone is Zone_Growing ? '=' : 'S', "区域");

            // 7. 地形 (fallback) — AltitudeLayer.Terrain(0.7)
            var terrain = map.terrainGrid.TerrainAt(pos);
            if (terrain != null)
                return (SymbolDictionary.GetChar(terrain), "地形");

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
