using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Verse;

namespace RimWorldMCP.AgentRuntime
{
    /// <summary>Agent 亲和性标记，控制 Tool 可见性。</summary>
    [Flags]
    public enum AgentAffinity
    {
        Overseer = 1,
        Economy  = 2,
        Combat   = 4,
        Medic    = 8
    }

    /// <summary>Agent 运行状态</summary>
    public enum AgentState { Sleeping, Running, WaitingExit }

    /// <summary>事件路由目标</summary>
    public enum EventRoute { Overseer, Economy, Combat, Medic, All, None }

    /// <summary>
    /// Agent 生命周期编排器。
    /// Scheduler 触发 → 构建 Context → 通过 CCB 发给 Claude → 处理响应。
    /// </summary>
    public static class AgentOrchestrator
    {
        /// <summary>当前活跃的 Agent（同时只能有一个非 Combat Agent）</summary>
        public static string? ActiveAgent { get; private set; }

        /// <summary>Combat Agent 是否正在运行</summary>
        public static bool IsCombatActive { get; set; }

        /// <summary>每个 Agent 的事件队列</summary>
        public static readonly ConcurrentDictionary<string, ConcurrentQueue<ColonyEvent>> AgentEvents = new();

        /// <summary>记录每个 Agent 上次运行时的游戏天数（用于检测新的一天）</summary>
        private static readonly Dictionary<string, int> _agentLastDay = new();

        /// <summary>Combat Agent 本轮循环次数（用于兜底退出）</summary>
        public static int CombatRoundCount { get; set; }
        public const int CombatMaxRounds = 10;       // 超过此轮数强制退出
        public const int CombatRemindRound = 6;       // 此轮开始追加退出提示

        static AgentOrchestrator()
        {
            foreach (var name in new[] { "overseer", "economy", "combat", "medic" })
                AgentEvents[name] = new ConcurrentQueue<ColonyEvent>();
        }

        /// <summary>获取当前游戏天数</summary>
        public static int CurrentDay =>
            Find.TickManager != null ? Find.TickManager.TicksGame / 60000 : 0;

        /// <summary>是否为新的一天（用于每日 Agent）</summary>
        public static bool IsNewDay(string agentName)
        {
            int day = CurrentDay;
            if (!_agentLastDay.TryGetValue(agentName, out var last) || day > last)
            {
                _agentLastDay[agentName] = day;
                return true;
            }
            return false;
        }

        /// <summary>标记 Agent 开始运行</summary>
        public static void BeginAgent(string agentName)
        {
            ActiveAgent = agentName;
            if (agentName == "combat")
            {
                IsCombatActive = true;
                CombatRoundCount = 0;
            }
            Scheduler.MarkWoken(agentName);
        }

        /// <summary>Agent 运行结束</summary>
        public static void EndAgent(string agentName)
        {
            if (agentName == "combat")
            {
                IsCombatActive = false;
                CombatRoundCount = 0;
            }
            if (ActiveAgent == agentName) ActiveAgent = null;
        }

        /// <summary>从 Agent 事件队列 drain 所有事件，生成 Prompt 文本</summary>
        public static string DrainEvents(string agentName)
        {
            if (!AgentEvents.TryGetValue(agentName, out var queue)) return "";
            var items = new List<string>();
            while (queue.TryDequeue(out var evt))
                items.Add($"- [{evt.Severity}] {evt.Summary}");
            return items.Count > 0 ? "## 最近事件\n" + string.Join("\n", items) : "";
        }

        /// <summary>分发事件到对应 Agent 的队列</summary>
        public static void DispatchEvent(ColonyEvent evt, EventRoute route)
        {
            switch (route)
            {
                case EventRoute.All:
                    foreach (var queue in AgentEvents.Values) queue.Enqueue(evt);
                    break;
                case EventRoute.None:
                    break;
                default:
                    var name = route.ToString().ToLower();
                    if (AgentEvents.TryGetValue(name, out var targetQueue))
                        targetQueue.Enqueue(evt);
                    break;
            }
        }
    }

    /// <summary>统一事件模型</summary>
    public class ColonyEvent
    {
        public string Category { get; set; } = "";      // Combat | Fire | Health | Food | Mood | Construction | Research | Economy | Quest
        public string Severity { get; set; } = "";      // Critical | Warning | Info
        public string Summary { get; set; } = "";
        public object? Payload { get; set; }
        public int Tick { get; set; }
    }
}
