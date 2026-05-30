using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RimWorldAgent.Core.AgentRuntime
{
    public enum AgentState { Sleeping, Running, WaitingExit }
    public enum EventRoute { Overseer, Economy, Combat, Medic, All, None }

    public static class AgentOrchestrator
    {
        public static string? ActiveAgent { get; private set; }
        public static bool IsCombatActive { get; set; }
        public static readonly ConcurrentDictionary<string, ConcurrentQueue<ColonyEvent>> AgentEvents = new();
        private static readonly Dictionary<string, int> _agentLastDay = new();
        private static readonly Dictionary<string, AgentState> _agentStates = new()
        {
            ["overseer"] = AgentState.Sleeping, ["economy"] = AgentState.Sleeping,
            ["combat"] = AgentState.Sleeping, ["medic"] = AgentState.Sleeping
        };

        /// <summary>真实游戏 TicksGame，由 SSE tick 事件自动更新</summary>
        public static int GameTick { get; set; }

        /// <summary>游戏天数，从 GameTick 计算</summary>
        public static int GameDay => GameTick / 60000;

        /// <summary>当前活跃角色显示名</summary>
        public static string AgentRoleDisplay
        {
            get
            {
                foreach (var kv in _agentStates)
                    if (kv.Value == AgentState.Running) return AgentDisplayName(kv.Key);
                return "休眠中";
            }
        }

        public static int CombatRoundCount { get; set; }
        public const int CombatMaxRounds = 10;
        public const int CombatRemindRound = 6;

        static AgentOrchestrator()
        {
            foreach (var name in new[] { "overseer", "economy", "combat", "medic" })
                AgentEvents[name] = new ConcurrentQueue<ColonyEvent>();
        }

        public static AgentState GetState(string agentName) =>
            _agentStates.TryGetValue(agentName, out var s) ? s : AgentState.Sleeping;

        public static bool IsSleeping(string agentName) => GetState(agentName) == AgentState.Sleeping;

        public static bool IsNewDay(string agentName)
        {
            int day = GameDay;
            if (!_agentLastDay.TryGetValue(agentName, out var last) || day > last) { _agentLastDay[agentName] = day; return true; }
            return false;
        }

        public static void BeginAgent(string agentName)
        {
            ActiveAgent = agentName;
            _agentStates[agentName] = AgentState.Running;
            if (agentName == "combat") { IsCombatActive = true; CombatRoundCount = 0; }
        }

        public static void EndAgent(string agentName)
        {
            _agentStates[agentName] = AgentState.Sleeping;
            if (agentName == "combat") { IsCombatActive = false; CombatRoundCount = 0; }
            if (ActiveAgent == agentName) ActiveAgent = null;
        }

        public static bool HasPendingEvents(string agentName)
            => AgentEvents.TryGetValue(agentName, out var q) && !q.IsEmpty;

        public static string DrainEvents(string agentName)
        {
            if (!AgentEvents.TryGetValue(agentName, out var queue)) return "";
            var items = new List<string>();
            while (queue.TryDequeue(out var evt)) items.Add($"- [{evt.Severity}] {evt.Summary}");
            return items.Count > 0 ? "## 最近事件\n" + string.Join("\n", items) : "";
        }

        public static void DispatchEvent(ColonyEvent evt, EventRoute route)
        {
            switch (route)
            {
                case EventRoute.All:
                    foreach (var q in AgentEvents.Values) q.Enqueue(evt); break;
                case EventRoute.None: break;
                default:
                    var name = route.ToString().ToLower();
                    if (AgentEvents.TryGetValue(name, out var tq)) tq.Enqueue(evt);
                    break;
            }
        }

        private static string AgentDisplayName(string name) => name switch
        {
            "overseer" => "总督 Overseer", "economy" => "生产经理 Economy",
            "combat" => "战斗指挥官 Combat", "medic" => "医疗官 Medic", _ => name
        };
    }

    public class ColonyEvent
    {
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Summary { get; set; } = "";
        public object? Payload { get; set; }
        public int Tick { get; set; }
        public string Route { get; set; } = "";
        public string Method { get; set; } = "";
    }
}
