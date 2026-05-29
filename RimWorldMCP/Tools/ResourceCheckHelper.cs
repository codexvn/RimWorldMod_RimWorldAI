using System.Collections.Generic;
using System.Text;
using Verse;

namespace RimWorldMCP.Tools
{
    public static class ResourceCheckHelper
    {
        /// <summary>
        /// 计算单个建造单位的材料需求
        /// </summary>
        /// <param name="def">建造定义</param>
        /// <param name="stuff">材料（MadeFromStuff 时）</param>
        /// <returns>材料 ThingDef → 需要数量 的字典；无成本时返回空字典</returns>
        public static Dictionary<ThingDef, int> CalculateCost(BuildableDef def, ThingDef? stuff)
        {
            var result = new Dictionary<ThingDef, int>();

            if (def.CostStuffCount > 0 && stuff != null)
            {
                result[stuff] = def.CostStuffCount;
            }

            if (def.CostList != null)
            {
                foreach (var entry in def.CostList)
                {
                    if (result.ContainsKey(entry.thingDef))
                        result[entry.thingDef] += entry.count;
                    else
                        result[entry.thingDef] = entry.count;
                }
            }

            return result;
        }

        /// <summary>
        /// 检查殖民地库存是否满足需求
        /// </summary>
        /// <param name="map">当前地图</param>
        /// <param name="needed">材料需求字典</param>
        /// <returns>null 表示充足，否则返回缺口明细文本</returns>
        public static string? CheckResources(Map map, Dictionary<ThingDef, int> needed)
        {
            if (needed.Count == 0) return null;

            var missing = new List<string>();
            var counter = map.resourceCounter;

            foreach (var kv in needed)
            {
                int have = counter?.GetCount(kv.Key) ?? 0;
                int need = kv.Value;
                if (have < need)
                {
                    missing.Add($"- {kv.Key.label} ({kv.Key.defName}): 需要 {need}, 现有 {have}, 缺 {need - have}");
                }
            }

            if (missing.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("资源不足，无法建造。资源缺口:");
            foreach (var line in missing)
                sb.AppendLine(line);
            return sb.ToString();
        }
    }
}
