using System.Text.Json;
using Verse;
using RimWorld;
using RimWorldMCP.Constants;

namespace RimWorldMCP.Harmony
{
    [StaticConstructorOnStartup]
    public static class Hook_Combat
    {
        private static readonly HarmonyLib.Harmony _instance = new("com.rimworldmcp.combat");

        static Hook_Combat()
        {
            _instance.Patch(
                original: HarmonyLib.AccessTools.Method(typeof(BattleLog), nameof(BattleLog.Add)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_Add_Postfix)));
            _instance.Patch(
                original: HarmonyLib.AccessTools.Method(typeof(DamageWorker.DamageResult), "AssociateWithLog"),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(AssociateWithLog_Postfix)));
            McpLog.Info("[Hook_Combat] BattleLog.Add + AssociateWithLog Hook 已注册");
        }

        /// <summary>AssociateWithLog 持有 log+damage，直接提取+推送。BattleLog.Add 仅负责非伤害事件（状态变更）。</summary>
        public static void AssociateWithLog_Postfix(DamageWorker.DamageResult __instance, LogEntry_DamageResult log)
        {
            try
            {
                var s = BattleLogCollector.Extract(log);
                if (s == null) return;

                s.ActualDamage = __instance.totalDamageDealt;
                s.RawDamage = __instance.totalDamageDealt;
                // damage type 从 text 已经覆盖；不单独设

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] AssociateWithLog 推送失败: {ex.Message}"); }
        }

        /// <summary>BattleLog.Add 仅负责非 DamageResult 事件（状态变更/倒地/死亡）</summary>
        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            if (entry is LogEntry_DamageResult) return; // 由 AssociateWithLog 处理
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] BattleLog.Add 推送失败: {ex.Message}"); }
        }
    }
}
