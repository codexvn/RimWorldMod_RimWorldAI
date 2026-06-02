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
            SafeLog.Info("[agent-harmony] MemoryUtility.ClearAllMapsAndWorld 即将执行 → 关闭 Agent");
            try
            {
                var gc = Current.Game?.GetComponent<GameComponent_RimWorldAgent>();
                if (gc != null)
                {
                    gc.ShutdownEngine();
                    return;
                }
                // Fallback: GameComponent 不可用时直接 Kill CCB
                SafeLog.Warning("[agent-harmony] 无法获取 GameComponent，直接 Kill CCB");
                CcbManager.KillStaleProcesses();
            }
            catch (Exception ex)
            {
                SafeLog.Error($"[agent-harmony] 关闭异常: {ex.Message}");
            }
        }
    }
}
