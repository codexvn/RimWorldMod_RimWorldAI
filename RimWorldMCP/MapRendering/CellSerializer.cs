using System.Collections.Generic;
using System.Text;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 多层单元格 → 文本序列化。展示关注点（base 层合并、[...] 触发规则）住在这里，
    /// 不住在数据模型 GridData 里。
    /// 序列化规则依赖 LayerCount / BaseLayerCount（每网格配置），故为实例而非纯静态。
    /// </summary>
    public class CellSerializer
    {
        public readonly int LayerCount;
        public readonly int BaseLayerCount;

        public CellSerializer(int layerCount, int baseLayerCount)
        {
            LayerCount = layerCount;
            BaseLayerCount = baseLayerCount;
        }

        /// <summary>
        /// 序列化一个单元格:
        /// 单层 → 单字符;
        /// 无上层 → 地面符号 (Plant ?? Terrain);
        /// 单上层 → 对象符{材质,数量} inline;
        /// 2+ 上层 → [上层1 上层2 ...]（底层不显式）
        /// </summary>
        public string Serialize(CellData cell)
        {
            if (LayerCount == 1)
                return cell.Layers[0]?.ObjectSymbol.ToString() ?? "?";

            // 地面符号: 从 BaseLayerCount-1 向下找第一个非 null（Plant 优先于 Terrain）
            char baseChar = '?';
            for (int i = BaseLayerCount - 1; i >= 0; i--)
            {
                if (cell.Layers[i].HasValue)
                {
                    baseChar = cell.Layers[i].Value.ObjectSymbol;
                    break;
                }
            }

            // 收集上层（含 ExtraBuildings）
            int upperCount = cell.ExtraBuildings?.Count ?? 0;
            for (int i = BaseLayerCount; i < LayerCount; i++)
                if (cell.Layers[i].HasValue) upperCount++;

            if (upperCount == 0)
                return baseChar.ToString();

            if (upperCount == 1)
            {
                // 单上层 → 对象符{注解} inline，无分组
                var layer = GetFirstUpper(cell);
                var sb = new StringBuilder();
                AppendLayer(sb, layer);
                return sb.ToString();
            }

            // 2+ 上层 → [上层1 上层2 ...]，底层不显式
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = BaseLayerCount; i < LayerCount; i++)
                {
                    if (!cell.Layers[i].HasValue) continue;
                    AppendLayer(sb, cell.Layers[i].Value);
                }
                if (cell.ExtraBuildings != null)
                {
                    foreach (var layer in cell.ExtraBuildings)
                        AppendLayer(sb, layer);
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        private CellLayer GetFirstUpper(CellData cell)
        {
            for (int i = BaseLayerCount; i < cell.Layers.Length; i++)
                if (cell.Layers[i].HasValue) return cell.Layers[i].Value;
            if (cell.ExtraBuildings != null && cell.ExtraBuildings.Count > 0)
                return cell.ExtraBuildings[0];
            return default;
        }

        /// <summary>序列化一个层: 对象符{材质,数量}</summary>
        private static void AppendLayer(StringBuilder sb, CellLayer layer)
        {
            sb.Append(layer.ObjectSymbol);
            bool hasAnno = layer.HasStuff || layer.Count > 0;
            if (!hasAnno) return;

            sb.Append('{');
            if (layer.HasStuff)
                sb.Append(layer.StuffSymbol!.Value);
            if (layer.Count > 0)
            {
                sb.Append(',');
                sb.Append(layer.Count);
            }
            sb.Append('}');
        }

        /// <summary>收集一个单元格中所有对象/材质符号到 sink</summary>
        public void CollectSymbols(CellData cell, HashSet<char> sink)
        {
            foreach (var layer in cell.Layers)
            {
                if (layer.HasValue)
                {
                    sink.Add(layer.Value.ObjectSymbol);
                    if (layer.Value.HasStuff)
                        sink.Add(layer.Value.StuffSymbol!.Value);
                }
            }
            if (cell.ExtraBuildings != null)
            {
                foreach (var layer in cell.ExtraBuildings)
                {
                    sink.Add(layer.ObjectSymbol);
                    if (layer.HasStuff)
                        sink.Add(layer.StuffSymbol!.Value);
                }
            }
        }
    }
}
