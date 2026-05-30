using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent
{
    internal class Program
    {
        private static readonly CancellationTokenSource _cts = new();

        public static async Task Main(string[] args)
        {
            // 设置控制台输出编码为 UTF-8
            Console.OutputEncoding = Encoding.UTF8;
            CoreLog.OnInfo = msg => Console.WriteLine($"[Core] {msg}");
            CoreLog.OnError = msg => Console.Error.WriteLine($"[Core] {msg}");

            var mcpUrl = "http://localhost:9878";
            var modelName = "";
            var planSpeed = "paused";
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if ((arg == "--model" || arg == "-m") && i + 1 < args.Length)
                    modelName = args[++i];
                else if (arg.StartsWith("--model="))
                    modelName = arg.Substring("--model=".Length);
                else if (arg.StartsWith("-m="))
                    modelName = arg.Substring("-m=".Length);
                else if (arg == "--plan-speed" && i + 1 < args.Length)
                    planSpeed = args[++i];
                else if (arg.StartsWith("--plan-speed="))
                    planSpeed = arg.Substring("--plan-speed=".Length);
                else if (!arg.StartsWith("-"))
                    mcpUrl = arg;
            }
            GamePaceController.PlanSpeed = planSpeed;
            if (!string.IsNullOrEmpty(modelName)) Console.WriteLine($"  模型: {modelName}");
            if (planSpeed != "paused") Console.WriteLine($"  Plan 速度: {planSpeed}");
            var sessionDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "claude-sessions", "dev-session");
            Directory.CreateDirectory(sessionDir);
            TaskBoard.SessionDir = sessionDir;

            Console.WriteLine($"RimWorldAgent 启动");
            Console.WriteLine($"  MCP: {mcpUrl}");
            Console.WriteLine($"  Session: {sessionDir}");

            // 启动 CCB 子进程（自动查找 cc-companion 目录）
            var ccbDir = FindCcbDir();
            CcbManager? ccb = null;
            if (!string.IsNullOrEmpty(ccbDir))
            {
                if (!CompanionInstaller.IsInstalled(ccbDir))
                {
                    Console.WriteLine("  CCB: npm install...");
                    await CompanionInstaller.InstallAsync(ccbDir);
                }
                ccb = new CcbManager(ccbDir!, sessionDir, modelName: modelName);
                if (ccb.Start()) { Console.WriteLine("  CCB: 启动中..."); await ccb.WaitReadyAsync(); Console.WriteLine("  CCB: 就绪"); }
            }
            if (ccb == null || !ccb.IsReady) Console.WriteLine("  CCB: 未启动 (Agent 将在无 CCB 模式运行)");

            var skillsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            InternalToolRegistry.Instance.LoadSkills(skillsDir);

            // Agent MCP Server (:9878) — 暴露内部 Tool 给 CCB
            var agentHost = new SimpleMspServer.McpServiceHost(9878, "localhost",
                    new SimpleMspServer.DelegateMspLog(Console.WriteLine));
            agentHost.RegisterProvider(InternalToolRegistry.Instance);
            agentHost.Start();
            Console.WriteLine("  AgentMCP: :9878 (内部 Tool + Skills)");

            using var mcp = new McpClient(mcpUrl);

            var ctx = new ContextBuilder(mcp);
            var loopInterval = TimeSpan.FromSeconds(10);

            Console.WriteLine("等待游戏启动...");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };

            // 等待 MCP 连接成功（游戏已启动）再开始 SSE 和 Agent 循环
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await mcp.CallTool("get_world_summary");
                    break;
                }
                catch (Exception ex)
                {
                    CoreLog.Info($"等待游戏启动: {ex.Message}，3s 后重试...");
                    await Task.Delay(3000, _cts.Token);
                }
            }
            if (_cts.IsCancellationRequested) return;

            // 游戏已启动，开始 SSE 事件监听 + Agent 主循环
            AgentLoop.WireEvents(mcp);
            mcp.StartSse();
            Console.WriteLine("Agent Main Loop 启动 (Ctrl+C 退出)");

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    // CCB 崩溃/空闲退出后自动重启
                    ccb?.TickAndRestart();

                    // 世界状态已由 SSE game/world-state 事件驱动更新（Scheduler.Tick 在 WireEvents 中）

                    var currentTick = AgentOrchestrator.GameTick;

                    // Overseer — 定时唤醒（唯一入口，其他角色由 Overseer 委托）
                    if (AgentOrchestrator.IsSleeping("overseer"))
                    {
                        bool shouldWake = Scheduler.ShouldWake("overseer", AgentConfigs.Overseer.IntervalGameHours, currentTick)
                            || AgentOrchestrator.IsNewDay("overseer");
                        if (shouldWake)
                        {
                            await RunAgentWithSwitchSupport(AgentConfigs.Overseer, ctx, mcp);
                        }
                    }

                    // Combat Agent — L3 Critical 事件驱动唤醒
                    if (AgentOrchestrator.IsSleeping("combat") && AgentOrchestrator.HasPendingEvents("combat"))
                    {
                        await RunAgentWithSwitchSupport(AgentConfigs.Combat, ctx, mcp);
                    }

                    await Task.Delay(loopInterval, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }

            Console.WriteLine("RimWorldAgent 退出");
        }

        private static async Task RunAgentViaCcb(AgentConfig config, string prompt, McpClient mcp)
        {
            using var ccbWs = new CcbWebSocket();

            if (!await ccbWs.ConnectAsync()) { CoreLog.Error($"[{config.Name}] CCB 连接失败"); return; }

            AgentLoop.WireCcbStatus(ccbWs);
            // 连接后立即推送当前角色状态
            if (ccbWs.IsReady)
                await ccbWs.SendEvent("agent.status", new { text = AgentOrchestrator.AgentRoleDisplay });

            await AgentLoop.RunSessionAsync(config, prompt, mcp, ccbWs);
        }

        /// <summary>运行 Agent 会话，结束后检查 switch_agent 请求并自动切换</summary>
        private static async Task RunAgentWithSwitchSupport(AgentConfig config, ContextBuilder ctx, McpClient mcp)
        {
            AgentOrchestrator.NextAgentRequest = null;
            AgentOrchestrator.BeginAgent(config.Name);
            Console.WriteLine($"=== {config.Name} 唤醒 (Load={Scheduler.LoadScore}, {Scheduler.Mode}) ===");

            var prompt = await ctx.BuildAsync(config);
            Console.WriteLine($"[Prompt] {prompt.Length} 字符");
            await RunAgentViaCcb(config, prompt, mcp);

            AgentOrchestrator.EndAgent(config.Name);
            Console.WriteLine($"=== {config.Name} 休眠 ===");

            // 检查 switch_agent 请求
            var nextAgent = AgentOrchestrator.NextAgentRequest;
            AgentOrchestrator.NextAgentRequest = null;
            if (!string.IsNullOrEmpty(nextAgent) && AgentOrchestrator.IsSleeping(nextAgent))
            {
                var nextConfig = AgentConfigs.Get(nextAgent);
                if (nextConfig != null)
                {
                    Console.WriteLine($"=== switch_agent → {nextAgent} ===");
                    await RunAgentWithSwitchSupport(nextConfig, ctx, mcp);
                }
            }
        }

        private static string? FindCcbDir()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // 优先 publish 打包版 (1.6/cc-companion), 回退源码目录
            var pub = Path.GetFullPath(Path.Combine(baseDir, "cc-companion"));
            var src = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "cc-companion"));
            if (Directory.Exists(pub) && File.Exists(Path.Combine(pub, "package.json"))) return pub;
            if (Directory.Exists(src) && File.Exists(Path.Combine(src, "package.json"))) return src;
            return null;
        }
    }
}
