using System;
using System.Collections.Generic;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 公共网格渲染器 — 矩形遍历生成网格。只做遍历，不含压缩/格式化。
    /// 两条独立管线（按数据类型，不强行统一模型）:
    ///   char 管线 — 单值 heatmap
    ///   CellData 管线 — 多层 tile_grid
    /// </summary>
    public static class GridRenderer
    {
        /// <summary>单值网格渲染结果（heatmap）</summary>
        public struct CharGridResult
        {
            public char[][] Rows;
            public HashSet<char> UsedSymbols;
        }

        /// <summary>单值网格: 每格返回一个 char（heatmap）</summary>
        public static CharGridResult RenderGrid(
            Map map,
            int minX, int minZ, int maxX, int maxZ,
            Func<IntVec3, Map, char> cellProvider)
        {
            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;

            var usedSymbols = new HashSet<char>();
            var rows = new char[h][];
            for (int z = 0; z < h; z++)
            {
                rows[z] = new char[w];
                for (int x = 0; x < w; x++)
                {
                    var pos = new IntVec3(minX + x, 0, minZ + z);
                    var symbol = cellProvider(pos, map);
                    rows[z][x] = symbol;
                    usedSymbols.Add(symbol);
                }
            }

            return new CharGridResult { Rows = rows, UsedSymbols = usedSymbols };
        }

        /// <summary>多层网格: 每格返回 CellData（get_tile_grid）。序列化器在此时按层配置创建。</summary>
        public static GridData RenderGrid(
            Map map,
            int minX, int minZ, int maxX, int maxZ,
            int layerCount,
            int baseLayerCount,
            Func<IntVec3, Map, CellData> cellProvider)
        {
            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;

            var grid = new GridData
            {
                LayerCount = layerCount,
                BaseLayerCount = baseLayerCount,
                UsedSymbols = new HashSet<char>(),
                Serializer = new CellSerializer(layerCount, baseLayerCount)
            };

            var rows = new CellData[h][];
            for (int z = 0; z < h; z++)
            {
                rows[z] = new CellData[w];
                for (int x = 0; x < w; x++)
                {
                    var pos = new IntVec3(minX + x, 0, minZ + z);
                    var cell = cellProvider(pos, map);
                    rows[z][x] = cell;
                    grid.Serializer.CollectSymbols(cell, grid.UsedSymbols);
                }
            }

            grid.Rows = rows;
            return grid;
        }
    }
}
