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

        /// <summary>AssociateWithLog: 推送带伤害数值的命中事件（命中/格挡/0伤害均覆盖）</summary>
        public static void AssociateWithLog_Postfix(DamageWorker.DamageResult __instance, LogEntry_DamageResult log)
        {
            try
            {
                var s = BattleLogCollector.Extract(log);
                if (s == null) return;

                s.ActualDamage = __instance.totalDamageDealt;
                s.RawDamage = __instance.totalDamageDealt;

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] AssociateWithLog 推送失败: {ex.Message}"); }
        }

        /// <summary>BattleLog.Add: 推送所有非 DamageResult 事件（倒地/死亡/状态变更）+ miss（AssociateWithLog 不触发的 DamageResult）</summary>
        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                // AssociateWithLog 已推送的（命中/格挡=有damagedParts或被格挡→Deflected）不重复
                // 仅 Miss（damagedParts==null && !Deflected）由此推送
                if (entry is LogEntry_DamageResult && (s.Deflected || (s.DamagedParts != null && s.DamagedParts.Count > 0)))
                    return;

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] BattleLog.Add 推送失败: {ex.Message}"); }
        }
    }
}
