using System;
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

        public static void AssociateWithLog_Postfix(DamageWorker.DamageResult __instance, LogEntry_DamageResult log)
        {
            try
            {
                System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                    $"[AssociateWithLog] dmg={__instance.totalDamageDealt} type={log.GetType().Name}\n");
                var s = BattleLogCollector.Extract(log);
                if (s == null) return;

                s.ActualDamage = __instance.totalDamageDealt;
                s.RawDamage = __instance.totalDamageDealt;

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                    $"[AssociateWithLog] pushed attacker={s.Attacker} defender={s.Defender} dmg={s.ActualDamage}\n");
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] AssociateWithLog 推送失败: {ex.Message}"); }
        }

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            bool isDamage = entry is LogEntry_DamageResult;
            System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                $"[BattleLog.Add] type={entry.GetType().Name} isDamage={isDamage}\n");
            if (isDamage) return;
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
