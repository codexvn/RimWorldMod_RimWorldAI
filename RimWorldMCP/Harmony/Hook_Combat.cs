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
        private static LogEntry? _pendingLog;   // 等待 AssociateWithLog 配对的 entry
        [ThreadStatic]
        private static float _pendingDamage;    // AssociateWithLog 存入的 totalDamageDealt

        static Hook_Combat()
        {
            _instance.Patch(
                original: HarmonyLib.AccessTools.Method(typeof(BattleLog), nameof(BattleLog.Add)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_Add_Postfix)));
            _instance.Patch(
                original: HarmonyLib.AccessTools.Method(typeof(BattleLog), "RemoveEntry"),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_RemoveEntry_Postfix)));
            _instance.Patch(
                original: HarmonyLib.AccessTools.Method(typeof(DamageWorker.DamageResult), "AssociateWithLog"),
                postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(AssociateWithLog_Postfix)));
            McpLog.Info("[Hook_Combat] BattleLog.Add/RemoveEntry + AssociateWithLog Hook 已注册");
        }

        // ---- RemoveEntry: 游戏删了 entry → 清理我们的标记 ----
        public static void BattleLog_RemoveEntry_Postfix(LogEntry entry)
        {
            if (ReferenceEquals(_pendingLog, entry))
                _pendingLog = null;
        }

        // ---- 核心：BattleLog.Add 先到 → 存 _pendingLog; Assoc 先到 → 存 _pendingDamage + _pendingLog ----
        public static void AssociateWithLog_Postfix(DamageWorker.DamageResult __instance, LogEntry_DamageResult log)
        {
            if (ReferenceEquals(_pendingLog, log) && _pendingDamage > 0)
            {
                // BattleLog 先到了→补 damage 推
                PushLogEntry(log, _pendingDamage);
                _pendingLog = null; _pendingDamage = 0;
                return;
            }
            // Assoc 先到→存 damage+log，等 BattleLog
            _pendingDamage = __instance.totalDamageDealt;
            _pendingLog = log;
        }

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                if (entry is LogEntry_DamageResult)
                {
                    if (ReferenceEquals(_pendingLog, entry) && _pendingDamage > 0)
                    {
                        // Assoc 先到了→推
                        PushLogEntry(entry, _pendingDamage);
                        _pendingLog = null; _pendingDamage = 0;
                        return;
                    }
                    // 同次攻击的第二条 DamageResult（无部位副本）→跳过
                    if (_pendingLog != null && !ReferenceEquals(_pendingLog, entry))
                    {
                        System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                            $"[BattleLog.Add] SKIP DmgResult2 entryHash={entry.GetHashCode()} pendingHash={_pendingLog?.GetHashCode()}\n");
                        return;
                    }
                    // BattleLog 先到→存着等 Assoc
                    _pendingLog = entry;
                    return;
                }
                // 非 DamageResult→直接推
                PushLogEntry(entry, 0);
            }
            catch (System.Exception ex) { McpLog.Warn($"[Hook_Combat] BattleLog.Add 推送失败: {ex.Message}"); }
        }

        private static void PushLogEntry(LogEntry entry, float damage)
        {
            var s = BattleLogCollector.Extract(entry);
            if (s == null) return;
            s.ActualDamage = damage; s.RawDamage = damage;
            var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
            System.IO.File.AppendAllText("F:/tmp_combat_debug.txt",
                $"[PUSH] entryHash={entry.GetHashCode()} type={entry.GetType().Name} attacker={s.Attacker}({s.AttackerId}) defender={s.Defender}({s.DefenderId}) dmg={s.ActualDamage} parts={string.Join(",",s.DamagedParts??new())}\n  SSE: {json}\n");
            McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
        }
    }
}
