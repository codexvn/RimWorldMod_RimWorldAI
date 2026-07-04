using System.Collections.Generic;

namespace RimWorldMCP.MapRendering
{
    public struct CellLayer
    {
        public char ObjectSymbol;
        public char? StuffSymbol;
        public int Count;
        public bool HasStuff => StuffSymbol.HasValue;
    }

    public struct CellData
    {
        public CellLayer?[] Layers;
        /// <summary>同一格多个 Building 时的溢出（index 2 只能放第一个）</summary>
        public List<CellLayer>? ExtraBuildings;
    }

    /// <summary>
    /// 多层单元格网格（row-major：Rows[z][x]）。
    /// 纯数据容器，序列化由独立的 CellSerializer 负责。
    /// get_tile_grid: 6 层 [Terrain, Plant, Building, Item, Pawn, Blueprint]
    /// Plant + Terrain = 地面层（BaseLayerCount=2），只显示 Plant ?? Terrain
    /// Building/Item/Pawn/Blueprint = 上层，触发 [...]
    /// </summary>
    public class GridData
    {
        public int LayerCount;
        public int BaseLayerCount;   // [0..BaseLayerCount) = 地面层
        public CellData[][] Rows;
        public HashSet<char> UsedSymbols;
        public CellSerializer Serializer;
    }
}
