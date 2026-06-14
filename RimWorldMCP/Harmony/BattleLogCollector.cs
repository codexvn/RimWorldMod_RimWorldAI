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
    /// 战斗日志收集器——提取指定 tick 范围内的战斗记录。
    /// </summary>
    public static class BattleLogCollector
    {
        public static int LastCollectTick { get; set; }
        public static void Reset() { LastCollectTick = 0; }

        /// <summary>获取从 LastCollectTick（不含）到当前 tick 之间的战斗记录</summary>
        public static List<CombatSummary> CollectSinceLast()
        {
            int sinceTick = LastCollectTick;
            int now = Find.TickManager?.TicksGame ?? 0;
            LastCollectTick = now;
            return Collect(sinceTick, now);
        }

        /// <summary>获取指定 tick 范围内的战斗记录</summary>
        public static List<CombatSummary> Collect(int sinceTick, int untilTick)
        {
            var results = new List<CombatSummary>();
            foreach (var battle in Find.BattleLog.Battles)
                foreach (var entry in battle.Entries)
                    if (entry.Tick > sinceTick && entry.Tick <= untilTick)
                        results.Add(MakeSummary(entry));
            return results.OrderBy(s => s.Tick).ToList();
        }

        /// <summary>快速摘要——仅提取 text + tick</summary>
        public static CombatSummary? Extract(LogEntry entry)
            => new CombatSummary { Tick = entry.Tick, Text = entry.ToGameStringFromPOV(null, false) };

        private static CombatSummary MakeSummary(LogEntry entry)
            => new CombatSummary { Tick = entry.Tick, Text = entry.ToGameStringFromPOV(null, false) };

        /// <summary>SSE payload</summary>
        public static object ToPayload(CombatSummary s) => new
        {
            type = "combat",
            text = s.Text,
            tick = s.Tick
        };

        /// <summary>生成人类可读文本报告</summary>
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

        /// <summary>SSE 批量推送（advance_tick 结束时）</summary>
        public static void PushAll(List<CombatSummary> summaries)
        {
            foreach (var s in summaries)
            {
                var json = JsonSerializer.Serialize(ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameNotification, json);
            }
        }
    }

    public class CombatSummary
    {
        public int Tick;
        public string Text = "";
    }
}
