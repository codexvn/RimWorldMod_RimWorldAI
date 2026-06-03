using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldMCP.Harmony
{
    /// <summary>降低 Plan 覆层透明度，仅保留隐约轮廓可见</summary>
    [StaticConstructorOnStartup]
    public static class Hook_PlanTransparency
    {
        static Hook_PlanTransparency()
        {
            var harmony = new HarmonyLib.Harmony("rimworld.mcp.plan_transparency");
            harmony.Patch(
                original: AccessTools.Method(typeof(Plan), "CreateMaterial"),
                postfix: new HarmonyMethod(typeof(Hook_PlanTransparency), nameof(Postfix_CreateMaterial)));
        }

        /// <summary>将 Plan 材质的 alpha 设为 0.12（原版默认 0.4）</summary>
        public static void Postfix_CreateMaterial(ref Material __result)
        {
            if (__result == null) return;
            var color = __result.color;
            color.a = 0.12f;
            __result.color = color;
        }
    }
}
