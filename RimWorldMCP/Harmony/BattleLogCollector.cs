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
    /// 战斗日志收集器——内存缓冲，不依赖 BattleLog.Battles 存活。
    /// </summary>
    public static class BattleLogCollector
    {
        /// <summary>消费者订阅列表</summary>
        private static readonly List<Action<CombatSummary>> _consumers = new();

        public static IDisposable Subscribe(Action<CombatSummary> consumer)
        {
            lock (_consumers) _consumers.Add(consumer);
            return new Unsubscriber(consumer, _consumers);
        }

        private class Unsubscriber : IDisposable
        {
            private readonly Action<CombatSummary> _consumer;
            private readonly List<Action<CombatSummary>> _consumers;
            public Unsubscriber(Action<CombatSummary> c, List<Action<CombatSummary>> cs) { _consumer = c; _consumers = cs; }
            public void Dispose() { lock (_consumers) _consumers.Remove(_consumer); }
        }

        /// <summary>Hook_Combat 调用——写入内存缓冲 + 分发全部订阅者</summary>
        public static void Publish(LogEntry entry)
        {
            var s = MakeSummary(entry);
            Action<CombatSummary>[] snapshot;
            lock (_consumers) snapshot = _consumers.ToArray();
            foreach (var consumer in snapshot)
                consumer(s);
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
