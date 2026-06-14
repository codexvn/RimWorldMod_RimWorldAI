using System;
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

        /// <summary>受击者 → (攻击者, 原始伤害, 实际伤害, 伤害类型标签)</summary>
        [ThreadStatic]
        private static (Pawn attacker, float amount, float dealt, string damageLabel)? _lastDamage;

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

        // ===== PostApplyDamage: 捕获伤害数字 =====

        public static void PostApplyDamage_Postfix(Pawn __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt <= 0f) return;
            var attacker = dinfo.Instigator as Pawn;
            _lastDamage = (attacker ?? __instance, dinfo.Amount, totalDamageDealt, dinfo.Def?.label ?? "?");
        }

        // ===== BattleLog.Add: 发送完整战斗事件 =====

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;

                // 桥接伤害数字（同帧同步，一对一）
                if (_lastDamage != null)
                {
                    s.RawDamage = _lastDamage.Value.amount;
                    s.ActualDamage = _lastDamage.Value.dealt;
                    s.DamageType = _lastDamage.Value.damageLabel;
                    _lastDamage = null;
                }

                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
            catch (Exception ex) { McpLog.Warn($"[Hook_Combat] 推送失败: {ex.Message}"); }
        }
    }
}
