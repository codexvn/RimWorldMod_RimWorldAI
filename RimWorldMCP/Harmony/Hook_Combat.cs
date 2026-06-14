using System;
using System.Collections.Concurrent;
using System.Text.Json;
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

        /// <summary>PostApplyDamage → BattleLog.Add 桥接队列（处理同帧多段攻击）</summary>
        [ThreadStatic]
        private static ConcurrentQueue<(Pawn attacker, float amount, float dealt, string damageLabel)>? _damageQueue;

        static Hook_Combat()
        {
            try
            {
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(BattleLog), nameof(BattleLog.Add)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_Add_Postfix)));
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(Pawn), nameof(Pawn.PostApplyDamage)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(PostApplyDamage_Postfix)));
                McpLog.Info("[Hook_Combat] BattleLog.Add + PostApplyDamage Hook 已注册");
            }
            catch (Exception ex) { McpLog.Error($"[Hook_Combat] 初始化失败: {ex.Message}"); }
        }

        // ===== PostApplyDamage: 捕获伤害数字，入队 =====

        public static void PostApplyDamage_Postfix(Pawn __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt <= 0f) return;
            var attacker = dinfo.Instigator as Pawn;
            if (_damageQueue == null) _damageQueue = new ConcurrentQueue<(Pawn, float, float, string)>();
            _damageQueue.Enqueue((attacker ?? __instance, dinfo.Amount, totalDamageDealt, dinfo.Def?.label ?? "?"));
        }

        // ===== BattleLog.Add: 发送完整战斗事件 =====

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                // 桥接伤害数字：PostApplyDamage 只在实际受伤时入队，未命中/格挡不入队
                // 只在有 damagedParts 时才出队（命中=有部位=有伤害入队，未命中=无部位=无入队）
                bool isHit = s.DamagedParts != null && s.DamagedParts.Count > 0 && !s.Deflected;
                if (isHit && _damageQueue != null && _damageQueue.TryDequeue(out var dmg))
                {
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
