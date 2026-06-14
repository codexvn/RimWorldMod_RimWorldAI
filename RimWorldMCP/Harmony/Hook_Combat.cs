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

        [ThreadStatic]
        private static float _pendingDamage;    // totalDamageDealt from AssociateWithLog
        [ThreadStatic]
        private static LogEntry? _pendingLog;   // 对账用——只在同一 entry 时取走

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
            if (__instance.totalDamageDealt <= 0f) return;
            _pendingDamage = __instance.totalDamageDealt;
            _pendingLog = log;
var st = new System.Diagnostics.StackTrace(true);
var sf = st.GetFrame(1);
System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
    $"[AssociateWithLog] dmg={__instance.totalDamageDealt} entry={log.GetHashCode()} caller={sf?.GetMethod()?.Name}\n");
        }

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                bool isSame = ReferenceEquals(_pendingLog, entry);
System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
    $"[BattleLog.Add] entry={entry.GetHashCode()} pendingLog={_pendingLog?.GetHashCode()} same={isSame} pendingDmg={_pendingDamage}\n");
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                // 同一 entry 才取走（同一调用栈同步）
                if (isSame)
                {
                    s.ActualDamage = _pendingDamage;
                    s.RawDamage = _pendingDamage;
                    _pendingDamage = 0;
                    _pendingLog = null;
                }

                var payload = BattleLogCollector.ToPayload(s);
                var json = JsonSerializer.Serialize(payload);
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] 推送失败: {ex.Message}"); }
        }
    }
}
