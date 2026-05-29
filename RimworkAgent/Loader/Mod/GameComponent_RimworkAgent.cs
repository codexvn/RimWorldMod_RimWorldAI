using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RimworkAgent.Core.AgentRuntime;
using RimworkAgent.Core.CcbManager;
using RimworkAgent.Core.Mcp;
using Verse;

namespace RimworkAgent
{
    public class GameComponent_RimworkAgent : GameComponent
    {
        private McpClient? _mcp;
        private CcbManager? _ccb;
        private ContextBuilder? _ctx;
        private bool _initialized;
        private int _lastTick;

        public GameComponent_RimworkAgent(Game game) { }

        public override void StartedNewGame() { base.StartedNewGame(); InitAgentRuntime(); }
        public override void LoadedGame() { base.LoadedGame(); InitAgentRuntime(); }

        private async void InitAgentRuntime()
        {
            if (_initialized) return;
            _initialized = true;

            CoreLog.OnInfo = msg => Log.Message($"[agent-core] {msg}");
            CoreLog.OnError = msg => Log.Error($"[agent-core] {msg}");

            var sessionDir = Path.Combine(
                Path.GetDirectoryName(typeof(GameComponent_RimworkAgent).Assembly.Location) ?? ".",
                "claude-sessions", "rimworld-agent");
            Directory.CreateDirectory(sessionDir);
            TaskBoard.SessionDir = sessionDir;

            var modRoot = Path.GetDirectoryName(typeof(GameComponent_RimworkAgent).Assembly.Location) ?? ".";
            var ccbDir = Path.Combine(modRoot, "..", "..", "..", "cc-companion");
            _ccb = new CcbManager(ccbDir, sessionDir);
            if (_ccb.Start()) await _ccb.WaitReadyAsync(15000);

            _mcp = new McpClient("http://localhost:9877");
            _mcp.OnGameEvent += evt =>
            {
                if (evt.Severity == "Critical" && evt.Category == "Combat")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Combat);
                else if (evt.Severity != "Critical")
                    AgentOrchestrator.DispatchEvent(evt, EventRoute.Overseer);
            };
            _mcp.StartSse();

            _ctx = new ContextBuilder(_mcp);
            _lastTick = 0;
            Log.Message("[agent-mod] Agent Runtime 初始化完成");
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            if (_mcp == null || _ctx == null || !_initialized) return;
            if (Find.CurrentMap == null) return;

            _lastTick++;
            if (_lastTick < 600) return;
            _lastTick = 0;
            _ = AgentTickAsync();
        }

        private async Task AgentTickAsync()
        {
            if (_mcp == null || _ctx == null) return;

            try
            {
                var summary = await _mcp.CallTool("get_world_summary");
                var input = ParseToSchedulerInput(summary);
                Scheduler.Tick(input);
                AgentOrchestrator.GameDay = input.CurrentTick / 60000;
            }
            catch { return; }

            var currentTick = Environment.TickCount;
            foreach (var config in AgentConfigs.All)
            {
                if (config.Name == "combat") continue;
                if (!AgentOrchestrator.IsSleeping(config.Name)) continue;

                bool shouldWake = false;
                if (config.IntervalGameHours > 0 && Scheduler.ShouldWake(config.Name, config.IntervalGameHours, currentTick))
                    shouldWake = true;
                if (config.TriggerDaily && AgentOrchestrator.IsNewDay(config.Name))
                    shouldWake = true;

                if (shouldWake)
                {
                    AgentOrchestrator.BeginAgent(config.Name);
                    Log.Message($"[agent-mod] 唤醒 {config.Name} (Load={Scheduler.LoadScore})");

                    var prompt = await _ctx.BuildAsync(config);
                    await RunAgentSession(config, prompt, _mcp);

                    AgentOrchestrator.EndAgent(config.Name);
                    Log.Message($"[agent-mod] {config.Name} 休眠");
                }
            }
        }

        private static async Task RunAgentSession(AgentConfig config, string prompt, McpClient mcp)
        {
            var tcs = new TaskCompletionSource<bool>();
            using var ccbWs = new CcbWebSocket();

            ccbWs.OnResult += (subtype, _) => { Log.Message($"[agent-mod] {config.Name} 结束: {subtype}"); tcs.TrySetResult(true); };
            ccbWs.OnToolUse += async (toolId, toolName, input) =>
            {
                await ToolDispatcher.HandleAsync(ccbWs, mcp, toolId, toolName, input,
                    msg => Log.Message($"[agent-mod] {msg}"));
            };

            if (!await ccbWs.ConnectAsync()) { Log.Warning($"[agent-mod] {config.Name} CCB 连接失败"); return; }

            try { await mcp.ListTools(config.Name); } catch { }

            await ccbWs.SendChat(prompt);
            var timeout = Task.Delay(config.Name == "combat" ? 300000 : 120000);
            await Task.WhenAny(tcs.Task, timeout);

            try { MemoryManager.Append(config.Name, new MemoryEntry { Day = AgentOrchestrator.GameDay, Insight = $"Load={Scheduler.LoadScore}({Scheduler.Mode})", Type = "session" }); } catch { }
        }

        private static SchedulerInput ParseToSchedulerInput(string text)
        {
            var input = new SchedulerInput { CurrentTick = Environment.TickCount };
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.Contains("殖民者") && t.Contains("|")) { int.TryParse(t.Split('|')[1].Trim(), out input.ColonistCount); }
                else if (t.Contains("食物") && t.Contains("天")) { float.TryParse(t.Split('|')[1].Trim().Replace("天", ""), out var f); input.FoodDays = f; }
                else if (t.Contains("敌人")) { int.TryParse(t.Split('|')[1].Trim(), out input.EnemyCount); }
            }
            return input;
        }

        public override void ExposeData() { base.ExposeData(); }
    }
}
