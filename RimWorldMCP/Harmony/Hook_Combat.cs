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

        /// <summary>最近一条由 AssociateWithLog 推送的 entry，BattleLog.Add 遇同一引用直接跳过</summary>
        [ThreadStatic]
        private static LogEntry? _pushedLog;

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
                var s = BattleLogCollector.Extract(log);
                if (s == null) return;

                s.ActualDamage = __instance.totalDamageDealt;
                s.RawDamage = __instance.totalDamageDealt;
                _pushedLog = log; // 标记已推送

                System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                    $"[AssociateWithLog] pushed attacker={s.Attacker} defender={s.Defender} dmg={s.ActualDamage} parts={string.Join(",",s.DamagedParts??new())}\n");
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, JsonSerializer.Serialize(BattleLogCollector.ToPayload(s)));
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] AssociateWithLog 推送失败: {ex.Message}"); }
        }

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                // AssociateWithLog 推送过的同一 entry → 跳过
                if (ReferenceEquals(_pushedLog, entry)) { _pushedLog = null; return; }

                // 游戏同一次攻击可生成两条 DamageResult entry：Assoc 推送第一条（带部位），这里遇到第二条（无部位）→ 跳过
                if (entry is LogEntry_DamageResult && _pushedLog != null)
                {
                    System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                        "[BattleLog.Add] SKIPPED duplicate DamageResult (Assoc already pushed for same tick)\n");
                    _pushedLog = null; // 清空标记，不影响后续
                    return;
                }

                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                    $"[BattleLog.Add] pushed type={entry.GetType().Name} attacker={s.Attacker}\n");
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, JsonSerializer.Serialize(BattleLogCollector.ToPayload(s)));
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] BattleLog.Add 推送失败: {ex.Message}"); }
        }
    }
}
