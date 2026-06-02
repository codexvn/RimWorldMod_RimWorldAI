using System;
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
            // 退出存档时 Kill CCB（用 Prefix 在 ClearAllMapsAndWorld 执行前拿 GameComponent）
            TryPatch(typeof(Verse.Profile.MemoryUtility), "ClearAllMapsAndWorld", nameof(Prefix_ClearAllMapsAndWorld));
        }

        private static void TryPatch(Type targetType, string methodName, string prefixMethod)
        {
            try
            {
                var original = AccessTools.Method(targetType, methodName);
                if (original == null)
                {
                    SafeLog.Warning($"[agent-harmony] 跳过 {targetType.FullName}.{methodName}: 方法不存在");
                    return;
                }
                _harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(HarmonyPatches), prefixMethod));
                SafeLog.Info($"[agent-harmony] Patch {targetType.Name}.{methodName} 成功");
            }
            catch (Exception ex)
            {
                SafeLog.Error($"[agent-harmony] Patch {targetType.FullName}.{methodName} 失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ===== 回调 =====

        public static void Prefix_ClearAllMapsAndWorld()
        {
            Log.Message("[agent-harmony] MemoryUtility.ClearAllMapsAndWorld → 关闭 Agent");
            try
            {
                Current.Game?.GetComponent<GameComponent_RimWorldAgent>()?.ShutdownEngine();
                SafeLog.Flush();
                CcbManager.KillStaleProcesses();
                Log.Message("[agent-harmony] 关闭完成");
            }
            catch (Exception ex)
            {
                Log.Error($"[agent-harmony] 关闭异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
