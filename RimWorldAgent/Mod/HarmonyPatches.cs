using System;
using HarmonyLib;
using Verse;

namespace RimWorldAgent
{
    [StaticConstructorOnStartup]
    public static class HarmonyPatches
    {
        private static readonly Harmony _harmony = new Harmony("RimWorldAgent");

        static HarmonyPatches()
        {
            // 退出存档时关闭 Agent（用 Prefix 在 ClearAllMapsAndWorld 执行前拿 GameComponent）
            TryPatch(typeof(Verse.Profile.MemoryUtility), "ClearAllMapsAndWorld", nameof(Prefix_ClearAllMapsAndWorld));
            // UIRoot.UIRootUpdate() 覆盖 Entry/Menu/Play 全部状态，每帧刷新 SafeLog
            TryPatch(typeof(UIRoot), "UIRootUpdate", nameof(Postfix_FlushSafeLog), postfix: true);
        }

        private static void TryPatch(Type targetType, string methodName, string patchMethod, bool postfix = false)
        {
            try
            {
                var original = AccessTools.Method(targetType, methodName);
                if (original == null)
                {
                    SafeLog.Warning($"[agent-harmony] 跳过 {targetType.FullName}.{methodName}: 方法不存在");
                    return;
                }
                var method = new HarmonyMethod(typeof(HarmonyPatches), patchMethod);
                _harmony.Patch(original,
                    prefix: postfix ? null : method,
                    postfix: postfix ? method : null);
                SafeLog.Info($"[agent-harmony] Patch {targetType.Name}.{methodName} 成功");
            }
            catch (Exception ex)
            {
                SafeLog.Error($"[agent-harmony] Patch {targetType.FullName}.{methodName} 失败: {FormatExceptionChain(ex)}");
            }
        }

        // ===== 回调 =====

        /// <summary>UIRoot 每帧 Postfix — 调用 SafeLog.Flush() 确保日志永不积压</summary>
        public static void Postfix_FlushSafeLog()
        {
            try { SafeLog.Flush(); }
            catch (Exception ex) { SafeLog.Error($"[agent-harmony] FlushSafeLog 异常: {FormatExceptionChain(ex)}"); }
        }

        public static void Prefix_ClearAllMapsAndWorld()
        {
            SafeLog.Info("[agent-harmony] MemoryUtility.ClearAllMapsAndWorld → 关闭 Agent");
            try
            {
                Current.Game?.GetComponent<GameComponent_RimWorldAgent>()?.ShutdownEngine();
                SafeLog.Info("[agent-harmony] 关闭完成");
            }
            catch (Exception ex)
            {
                SafeLog.Error($"[agent-harmony] 关闭异常: {FormatExceptionChain(ex)}\n{ex.StackTrace}");
            }
            finally
            {
                SafeLog.Flush();
            }
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
