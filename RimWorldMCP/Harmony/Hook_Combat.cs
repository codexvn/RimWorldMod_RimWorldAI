using System.Runtime.CompilerServices;
using System.Text.Json;
using Verse;
using RimWorld;
using RimWorldMCP.Constants;

namespace RimWorldMCP.Harmony
{
    /// <summary>
    /// BattleLog.Add + AssociateWithLog 双拦截 → 详细战斗事件 SSE 推送。
    /// AssociateWithLog 同时持有 LogEntry + DamageResult → 自然一对一，无需队列/计数器。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Hook_Combat
    {
        private static readonly HarmonyLib.Harmony _instance = new HarmonyLib.Harmony("com.rimworldmcp.combat");

        /// <summary>LogEntry → (totalDamageDealt, rawAmount, damageLabel)</summary>
        private static readonly ConditionalWeakTable<LogEntry, DamageSnapshot> _snapshots = new();

        private class DamageSnapshot
        {
            public float TotalDamageDealt;
            public float RawAmount;
            public string? DamageLabel;
        }

        static Hook_Combat()
        {
            try
            {
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(BattleLog), nameof(BattleLog.Add)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_Add_Postfix)));
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(DamageWorker.DamageResult), nameof(DamageWorker.DamageResult.AssociateWithLog)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(AssociateWithLog_Postfix)));
                McpLog.Info("[Hook_Combat] BattleLog.Add + AssociateWithLog Hook 已注册");
            }
            catch (System.Exception ex) { McpLog.Error($"[Hook_Combat] 初始化失败: {ex.Message}"); }
        }

        // ===== AssociateWithLog: 提取 damage 数据，关联到 LogEntry =====

        public static void AssociateWithLog_Postfix(DamageWorker.DamageResult __instance, LogEntry_DamageResult log)
        {
            if (__instance.totalDamageDealt <= 0f) return;
            _snapshots.Add(log, new DamageSnapshot
            {
                TotalDamageDealt = __instance.totalDamageDealt,
                RawAmount = 0, // not available here; use total
                DamageLabel = null
            });
        }

        // ===== BattleLog.Add: 发送完整战斗事件 =====

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                if (_snapshots.TryGetValue(entry, out var snap))
                {
                    s.RawDamage = snap.TotalDamageDealt;
                    s.ActualDamage = snap.TotalDamageDealt;
                    s.DamageType = snap.DamageLabel;
                    _snapshots.Remove(entry);
                }

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] 推送失败: {ex.Message}"); }
        }
    }
}
