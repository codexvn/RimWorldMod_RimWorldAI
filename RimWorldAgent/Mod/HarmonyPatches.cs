using System;
using System.Linq;
using HarmonyLib;
using RimWorldAgent.Core.CcbManager;
using Verse;

namespace RimWorldAgent
{
    [StaticConstructorOnStartup]
    public static class HarmonyPatches
    {
        private static readonly Harmony _harmony = new Harmony("RimWorldAgent");

        static HarmonyPatches()
        {
            try
            {
                Log.Message("[agent-harmony] 开始安装 Harmony 补丁...");
                _harmony.PatchAll();
                var patched = _harmony.GetPatchedMethods().ToList();
                Log.Message($"[agent-harmony] Harmony 补丁已安装，共 {patched.Count} 个方法");
                foreach (var m in patched)
                    Log.Message($"[agent-harmony]   - {m.DeclaringType?.FullName}.{m.Name}");
            }
            catch (Exception ex)
            {
                Log.Error($"[agent-harmony] 安装 Harmony 失败: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// <see cref="Game.ClearAllMapsAndWorld"/> — 退出当前游戏（返回主菜单）。
    /// 与 <see cref="Game.DeinitAndRemoveMap"/> 不同，后者是 Map 类方法且不被调用。
    /// </summary>
    [HarmonyPatch(typeof(Game), "ClearAllMapsAndWorld")]
    public static class Patch_Game_ClearAllMapsAndWorld
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            Log.Message("[agent-harmony] Game.ClearAllMapsAndWorld → 退出到主菜单");
            try
            {
                var gc = Current.Game?.GetComponent<GameComponent_RimWorldAgent>();
                if (gc != null)
                {
                    gc.ShutdownEngine();
                    return;
                }
                Log.Warning("[agent-harmony] 无法获取 GameComponent 实例，仅 Kill CCB");
                CcbManager.KillStaleProcesses();
            }
            catch (Exception ex)
            {
                Log.Error($"[agent-harmony] 退出存档关闭异常: {ex.Message}");
            }
        }
    }
}
