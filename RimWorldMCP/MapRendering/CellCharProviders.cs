using System;
using System.Linq;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 各网格工具的单元格映射函数，统一查 SymbolDictionary。
    /// 两条独立管线（不强行统一模型）:
    ///   get_tile_grid → ForTileGrid: 返回 CellData（多层），渲染优先级匹配游戏 AltitudeLayer
    ///     蓝图(9.5) > Pawn(8.4) > 物品(6.6) > 建筑(5.5) > 植物(4.0) > 地形(0.7)
    ///   heatmap → ForTerrain/Fertility/Temperature/Pollution: 返回 char（单值）
    /// </summary>
    public static class CellCharProviders
    {
        public const int TileGridLayerCount = 6;
        public const int TileGridBaseLayerCount = 2; // Terrain + Plant

        public static CellData ForTileGrid(IntVec3 pos, Map map)
        {
            var layers = new CellLayer?[TileGridLayerCount];

            // Layer 0: Terrain (地面数据)
            var terrain = map.terrainGrid.TerrainAt(pos);
            if (terrain != null)
                layers[0] = new CellLayer { ObjectSymbol = SymbolDictionary.GetChar(terrain) };
            else if (!pos.Fogged(map))
                layers[0] = new CellLayer { ObjectSymbol = '?' };

            if (pos.Fogged(map))
            {
                layers[0] = new CellLayer { ObjectSymbol = '█' };
                return new CellData { Layers = layers };
            }

            var thingsAtPos = pos.GetThingList(map);
            List<CellLayer>? extraBuildings = null;

            // Layer 1: Plant (地面显示)
            var plant = pos.GetPlant(map);
            if (plant != null)
                layers[1] = new CellLayer { ObjectSymbol = SymbolDictionary.GetChar(plant.def) };

            // Layer 2: Building (所有 Building，不止 edifice；多个时 EnterBuildings 溢出)
            if (thingsAtPos != null)
            {
                foreach (var t in thingsAtPos)
                {
                    if (t.def.category != ThingCategory.Building) continue;
                    if (t is Blueprint || t is Frame) continue; // 蓝图层独立

                    var layer = new CellLayer { ObjectSymbol = SymbolDictionary.GetChar(t.def) };
                    if (t.Stuff != null)
                        layer.StuffSymbol = SymbolDictionary.GetChar(t.Stuff);

                    if (!layers[2].HasValue)
                        layers[2] = layer;
                    else
                    {
                        if (extraBuildings == null) extraBuildings = new List<CellLayer>();
                        extraBuildings.Add(layer);
                    }
                }
            }

            // Layer 3: Items (按 def 聚合，取数量最多的)
            if (thingsAtPos != null)
            {
                var itemGroups = new Dictionary<string, (int count, ThingDef def, Thing? stuffed)>();
                foreach (var t in thingsAtPos)
                {
                    if (t.def.category != ThingCategory.Item && !(t is Corpse)) continue;
                    var defName = t.def.defName;
                    if (itemGroups.ContainsKey(defName))
                    {
                        var existing = itemGroups[defName];
                        itemGroups[defName] = (existing.count + (t is Corpse ? 1 : t.stackCount), existing.def, existing.stuffed ?? (t.Stuff != null ? t : null));
                    }
                    else
                    {
                        itemGroups[defName] = ((t is Corpse ? 1 : t.stackCount), t.def, t.Stuff != null ? t : null);
                    }
                }

                if (itemGroups.Count > 0)
                {
                    var best = itemGroups.OrderByDescending(kvp => kvp.Value.count).First();
                    var def = best.Value.def;
                    var itemLayer = new CellLayer
                    {
                        ObjectSymbol = SymbolDictionary.GetChar(def),
                        Count = best.Value.count
                    };
                    if (best.Value.stuffed != null)
                        itemLayer.StuffSymbol = SymbolDictionary.GetChar(best.Value.stuffed.Stuff);
                    layers[3] = itemLayer;
                }
            }

            // Layer 4: Pawn
            if (thingsAtPos != null)
            {
                var pawn = thingsAtPos.FirstOrDefault(t => t is Pawn);
                if (pawn != null)
                    layers[4] = new CellLayer { ObjectSymbol = SymbolDictionary.GetChar(pawn.def) };
            }

            // Layer 5: Blueprint/Frame
            if (thingsAtPos != null)
            {
                var blueprint = thingsAtPos.FirstOrDefault(t => t is Blueprint || t is Frame);
                if (blueprint != null)
                    layers[5] = new CellLayer { ObjectSymbol = '∎' };
            }

            return new CellData { Layers = layers, ExtraBuildings = extraBuildings };
        }

        // ====== heatmap (单值，返回 char) ======

        public static char ForTerrain(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map)) return '?';
            var terrain = map.terrainGrid.TerrainAt(pos);
            return terrain != null ? SymbolDictionary.GetChar(terrain) : '?';
        }

        public static char ForFertility(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map)) return '?';
            float f = map.fertilityGrid.FertilityAt(pos) * 100f;
            return f >= 140 ? '▓' : f >= 100 ? '▒' : f >= 70 ? '░' : '·';
        }

        public static char ForTemperature(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map)) return '?';
            float t = GenTemperature.GetTemperatureForCell(pos, map);
            return t < -20 ? '█' : t < 0 ? '▓' : t < 10 ? '░' : t < 21 ? '.' : t < 35 ? '○' : t < 60 ? '◎' : '●';
        }

        public static char ForPollution(IntVec3 pos, Map map)
        {
            if (pos.Fogged(map)) return '?';
            return map.pollutionGrid.IsPolluted(pos) ? 'P' : '.';
        }
    }
}
