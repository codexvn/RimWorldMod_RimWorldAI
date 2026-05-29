using System.Linq;
using Verse;

namespace RimWorldMCP.Helpers
{
    public static class BuildingMaterialHelper
    {
        /// <summary>动态获取所有可用建筑材料的 defName 列表（含 Mod 添加的材料）</summary>
        public static string[] GetStuffEnum()
        {
            try
            {
                var stuffs = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.IsStuff && !d.defName.StartsWith("_"))
                    .Select(d => d.defName)
                    .OrderBy(n => n)
                    .ToArray();
                return stuffs.Length > 0 ? stuffs : new[] { "Steel", "WoodLog", "Plasteel", "GraniteBlocks" };
            }
            catch
            {
                return new[] { "Steel", "WoodLog", "Plasteel", "GraniteBlocks" };
            }
        }
    }
}
