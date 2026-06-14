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
        private static LogEntry? _pushedLog;      // AssociateWithLog 推送过的 entry
        [ThreadStatic]
        private static float _pushedDamage;

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
                // 如果同一 entry 已被 BattleLog 先推过(无 damage)，重新推送带 damage 版本
                if (ReferenceEquals(_pushedLog, log))
                {
                    _pushedLog = null; // 清除标记，只重推一次
                    var s = BattleLogCollector.Extract(log);
                    if (s == null) return;
                    s.ActualDamage = __instance.totalDamageDealt;
                    s.RawDamage = __instance.totalDamageDealt;
                    var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                    System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                        $"[AssociateWithLog] REPUSH(w/dmg) logRef={log.GetHashCode()} attacker={s.Attacker}({s.AttackerId}) dmg={s.ActualDamage}\n  SSE: {json}\n");
                    McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
                    return;
                }

                // 正常路径：AssociateWithLog 先于 BattleLog.Add
                var s2 = BattleLogCollector.Extract(log);
                if (s2 == null) return;
                s2.ActualDamage = __instance.totalDamageDealt;
                s2.RawDamage = __instance.totalDamageDealt;
                _pushedLog = log;
                _pushedDamage = __instance.totalDamageDealt;

                var json2 = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s2));
                System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                    $"[AssociateWithLog] PUSH logRef={log.GetHashCode()} attacker={s2.Attacker}({s2.AttackerId}) defender={s2.Defender}({s2.DefenderId}) dmg={s2.ActualDamage}\n  SSE: {json2}\n");
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json2);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] AssociateWithLog 推送失败: {ex.Message}"); }
        }

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                // 已推送过 → 跳过
                if (ReferenceEquals(_pushedLog, entry)) { _pushedLog = null; return; }

                // 同次攻击第二条 → 跳过
                if (entry is LogEntry_DamageResult && _pushedLog != null)
                {
                    System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                        $"[BattleLog.Add] SKIP(DmgResult2) entryHash={entry.GetHashCode()} pushedHash={_pushedLog?.GetHashCode()}\n");
                    _pushedLog = null;
                    return;
                }

                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                    $"[BattleLog.Add] PUSH entryHash={entry.GetHashCode()} type={entry.GetType().Name} attacker={s.Attacker}({s.AttackerId}) dmg={s.ActualDamage}\n  SSE: {json}\n");
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] BattleLog.Add 推送失败: {ex.Message}"); }
        }
    }
}
