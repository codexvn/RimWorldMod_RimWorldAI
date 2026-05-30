using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RimWorldAgent.Core.AgentRuntime
{
    public enum AgentState { Sleeping, Running, WaitingExit }
    public enum EventRoute { Overseer, Economy, Combat, Medic, All, None }
    public enum GamePhase { None, Plan, Act }

    public static class AgentOrchestrator
    {
        public static string? ActiveAgent { get; private set; }
        public static bool IsCombatActive { get; set; }

        /// <summary>Agent 状态变化时触发，参数为当前角色显示名（如 "Overseer" 或 "休眠中"）</summary>
        public static event Action<string>? OnStatusChanged;
        public static readonly ConcurrentDictionary<string, ConcurrentQueue<ColonyEvent>> AgentEvents = new();
        private static readonly ConcurrentDictionary<string, List<string>> _advices = new();
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
                string role;
                foreach (var kv in _agentStates)
                    if (kv.Value == AgentState.Running) { role = AgentDisplayName(kv.Key); goto found; }
                return "休眠中";
                found:
                var phase = CurrentPhase switch { GamePhase.Plan => " [Plan]", GamePhase.Act => " [Act]", _ => "" };
                return role + phase;
            }
        }

        public static int CombatRoundCount { get; set; }
        public const int CombatMaxRounds = 10;
        public const int CombatRemindRound = 6;

        /// <summary>switch_agent 请求的目标 Agent，主循环会话结束后检查并切换</summary>
        public static string? NextAgentRequest { get; set; }

        /// <summary>当前游戏阶段（Plan/Act/None）</summary>
        public static GamePhase CurrentPhase { get; private set; }

        /// <summary>当前会话的 GamePaceController 实例（由 RunSessionAsync 设置）</summary>
        public static GamePaceController? PaceController { get; set; }

        /// <summary>当前会话的 McpClient（供内部 Tool 调用 MCP）</summary>
        public static Mcp.McpClient? SessionMcp { get; set; }

        public static void EnterPlanPhase()
        {
            CurrentPhase = GamePhase.Plan;
            OnStatusChanged?.Invoke(AgentRoleDisplay);
        }

        public static void EnterActPhase()
        {
            CurrentPhase = GamePhase.Act;
            OnStatusChanged?.Invoke(AgentRoleDisplay);
        }

        public static void ClearPhase()
        {
            CurrentPhase = GamePhase.None;
            OnStatusChanged?.Invoke(AgentRoleDisplay);
        }

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
            OnStatusChanged?.Invoke(AgentRoleDisplay);
        }

        public static void EndAgent(string agentName)
        {
            _agentStates[agentName] = AgentState.Sleeping;
            if (agentName == "combat") { IsCombatActive = false; CombatRoundCount = 0; }
            if (ActiveAgent == agentName) ActiveAgent = null;
            OnStatusChanged?.Invoke(AgentRoleDisplay);
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

        public static void AddAdvice(string targetRole, string advice)
        {
            _advices.AddOrUpdate(targetRole,
                _ => new List<string> { advice },
                (_, list) => { lock (list) { list.Add(advice); } return list; });
        }

        public static List<string> DrainAdvices(string role)
        {
            if (_advices.TryRemove(role, out var list)) return list;
            return new List<string>();
        }

        /// <summary>Agent 侧事件路由（原 NotificationBus.GetEventAgent，迁移至此）</summary>
        public static EventRoute RouteEvent(string category, string severity)
        {
            // L3 Critical → 按 Category 路由
            if (severity == "Critical")
                return category switch
                {
                    "Combat" => EventRoute.Combat,
                    "Health" => EventRoute.Medic,
                    _ => EventRoute.Combat
                };

            // L1-L2 → 按 Category 路由
            return category switch
            {
                "Food" or "Medicine" or "Resources" => EventRoute.All,
                "Economy" or "Research" or "Mood" => EventRoute.Overseer,
                "Construction" => EventRoute.Economy,
                _ => EventRoute.Overseer
            };
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
        public string Method { get; set; } = "";
    }
}
