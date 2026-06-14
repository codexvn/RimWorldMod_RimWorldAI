using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Verse;
using RimWorld;
using RimWorldMCP.Constants;

namespace RimWorldMCP
{
    /// <summary>
    /// 战斗日志收集器——提取 attacker/defender/weapon + 游戏文本。
    /// </summary>
    public static class BattleLogCollector
    {
        public static int LastCollectTick { get; set; }
        public static void Reset() { LastCollectTick = 0; }

        public static List<CombatSummary> Collect(int sinceTick, int untilTick)
        {
            var results = new List<CombatSummary>();
            foreach (var battle in Find.BattleLog.Battles)
                foreach (var entry in battle.Entries)
                    if (entry.Tick > sinceTick && entry.Tick <= untilTick)
                        results.Add(MakeSummary(entry));
            return results.OrderBy(s => s.Tick).ToList();
        }

        public static CombatSummary? Extract(LogEntry entry) => MakeSummary(entry);

        private static CombatSummary MakeSummary(LogEntry entry)
        {
            var s = new CombatSummary { Tick = entry.Tick, Text = entry.ToGameStringFromPOV(null, false) };
            if (entry is BattleLogEntry_RangedImpact) s.Type = "ranged";
            else if (entry is BattleLogEntry_MeleeCombat) s.Type = "melee";
            else if (entry is BattleLogEntry_StateTransition) s.Type = "state";
            else s.Type = "other";
            return s;
        }

        public static object ToPayload(CombatSummary s) => new
        {
            type = "combat",
            attack_type = s.Type,
            text = s.Text,
            tick = s.Tick
        };

        public static string BuildTextReport(List<CombatSummary> summaries)
        {
            if (summaries.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("### 战斗日志");
            foreach (var s in summaries)
                sb.AppendLine($"- (Tick {s.Tick}) {s.Text}");
            return sb.ToString().TrimEnd();
        }

        public static void PushAll(List<CombatSummary> summaries)
        {
            foreach (var s in summaries)
            {
                var json = JsonSerializer.Serialize(ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
        }
    }

    public class CombatSummary
    {
        public int Tick;
        public string Text = "";
        public string Type = "";
    }
}
