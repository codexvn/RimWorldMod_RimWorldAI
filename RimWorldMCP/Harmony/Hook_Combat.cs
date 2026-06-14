using System;
using System.Text.Json;
using Verse;
using RimWorld;
using RimWorldMCP.Constants;

namespace RimWorldMCP.Harmony
{
    /// <summary>BattleLog.Add 拦截 → 战斗事件 SSE 实时推送</summary>
    [StaticConstructorOnStartup]
    public static class Hook_Combat
    {
        private static readonly HarmonyLib.Harmony _instance = new HarmonyLib.Harmony("com.rimworldmcp.combat");

        static Hook_Combat()
        {
            try
            {
                _instance.Patch(
                    original: HarmonyLib.AccessTools.Method(typeof(BattleLog), nameof(BattleLog.Add)),
                    postfix: new HarmonyLib.HarmonyMethod(typeof(Hook_Combat), nameof(BattleLog_Add_Postfix)));
                McpLog.Info("[Hook_Combat] BattleLog.Add Hook 已注册");
            }
            catch (Exception ex) { McpLog.Error($"[Hook_Combat] 初始化失败: {ex.Message}"); }
        }

        public static void BattleLog_Add_Postfix(LogEntry entry)
        {
            try
            {
                var s = BattleLogCollector.Extract(entry);
                if (s == null) return;
                var json = JsonSerializer.Serialize(BattleLogCollector.ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameNotification, json);
            }
            catch (Exception ex) { McpLog.Warn($"[Hook_Combat] 推送失败: {ex.Message}"); }
        }
    }
}
