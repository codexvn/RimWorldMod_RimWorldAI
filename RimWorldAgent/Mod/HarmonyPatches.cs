using System;
using System.Linq;
using System.Reflection;
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
            // 纯日志：验证调用时机
            TryPatch(typeof(Game), "DeinitAndRemoveMap", nameof(Postfix_DeinitAndRemoveMap));

            // 主功能：退出存档时 Kill CCB
            TryPatch(typeof(Verse.Profile.MemoryUtility), "ClearAllMapsAndWorld", nameof(Postfix_ClearAllMapsAndWorld));
        }

        private static void TryPatch(Type targetType, string methodName, string postfixMethod)
        {
            try
            {
                var original = AccessTools.Method(targetType, methodName);
                if (original == null)
                {
                    var allMethods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Select(m => m.Name).Distinct().Take(20);
                    Log.Warning($"[agent-harmony] 跳过 {targetType.FullName}.{methodName}: 方法不存在 (前20: {string.Join(", ", allMethods)})");
                    return;
                }
                _harmony.Patch(original, postfix: new HarmonyMethod(typeof(HarmonyPatches), postfixMethod));
                Log.Message($"[agent-harmony] Patch {targetType.Name}.{methodName} 成功");
            }
            catch (Exception ex)
            {
                Log.Error($"[agent-harmony] Patch {targetType.FullName}.{methodName} 失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ===== 回调方法 =====

        public static void Postfix_DeinitAndRemoveMap()
        {
            Log.Message("[agent-harmony] Game.DeinitAndRemoveMap 被调用");
        }

        public static void Postfix_ClearAllMapsAndWorld()
        {
            Log.Message("[agent-harmony] MemoryUtility.ClearAllMapsAndWorld → 退出到主菜单");
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
