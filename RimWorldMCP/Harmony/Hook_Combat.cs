using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using Verse;
using RimWorld;
using RimWorldMCP.Constants;

namespace RimWorldMCP.Harmony
{
    /// <summary>BattleLog.Add + PostApplyDamage 双拦截 → 详细战斗事件 SSE 推送</summary>
    [StaticConstructorOnStartup]
    public static class Hook_Combat
    {
        private static readonly HarmonyLib.Harmony _instance = new HarmonyLib.Harmony("com.rimworldmcp.combat");

        /// <summary>PostApplyDamage → BattleLog.Add 桥接队列 + 计数器</summary>
        [ThreadStatic]
        private static ConcurrentQueue<(Pawn attacker, float amount, float dealt, string damageLabel)>? _damageQueue;
        [ThreadStatic]
        private static int _damagePending; // PostApplyDamage入队+1, BattleLog出队-1

        static Hook_Combat()
        {
            try
            {
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(BattleLog), nameof(BattleLog.Add)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_Add_Postfix)));
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(Thing), nameof(Thing.PostApplyDamage)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(PostApplyDamage_Postfix)));
                McpLog.Info("[Hook_Combat] BattleLog.Add + PostApplyDamage(Thing) Hook 已注册");
            }
            catch (Exception ex) { McpLog.Error($"[Hook_Combat] 初始化失败: {ex.Message}"); }
        }

        // ===== PostApplyDamage: Pawn + Thing 双入口，存入计数器+队列 =====

        public static void PostApplyDamage_Postfix(Thing __instance, DamageInfo dinfo, float totalDamageDealt)
            => EnqueueDamage(dinfo, totalDamageDealt);

        private static void EnqueueDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt <= 0f) return;
            if (_damageQueue == null) _damageQueue = new ConcurrentQueue<(Pawn, float, float, string)>();
            _damageQueue.Enqueue((dinfo.Instigator as Pawn, dinfo.Amount, totalDamageDealt, dinfo.Def?.label ?? "?"));
            _damagePending++;
        }

        // ===== BattleLog.Add: 发送完整战斗事件 + 计数器出队 =====

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                // 计数器驱动出队——对 Pawn 和建筑都生效
                if (_damagePending > 0 && _damageQueue != null && _damageQueue.TryDequeue(out var dmg))
                {
                    Interlocked.Decrement(ref _damagePending);
                    s.RawDamage = dmg.amount;
                    s.ActualDamage = dmg.dealt;
                    s.DamageType = dmg.damageLabel;
                }
                else
                {
                    s.RawDamage = 0;
                    s.ActualDamage = 0;
                }

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (Exception ex) { McpLog.Warn($"[Hook_Combat] 推送失败: {ex.Message}"); }
        }
    }
}
