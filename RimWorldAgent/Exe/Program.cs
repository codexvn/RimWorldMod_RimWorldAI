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
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if ((arg == "--model" || arg == "-m") && i + 1 < args.Length)
                    modelName = args[++i];
                else if (arg.StartsWith("--model="))
                    modelName = arg.Substring("--model=".Length);
                else if (arg.StartsWith("-m="))
                    modelName = arg.Substring("-m=".Length);
                else if (!arg.StartsWith("-"))
                    mcpUrl = arg;
            }
            if (!string.IsNullOrEmpty(modelName)) Console.WriteLine($"  模型: {modelName}");
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
            InternalToolRegistry.Instance.InitializeSkillTools();

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
                    // 1. 获取世界状态 → 更新 Scheduler
                    try
                    {
                        var summary = await mcp.CallTool("get_world_summary");
                        var input = AgentLoop.ParseSchedulerInput(summary);
                        Scheduler.Tick(input);
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Error($"MCP get_world_summary 失败: {ex.Message}");
                        await Task.Delay(5000, _cts.Token);
                        continue;
                    }

                    // 2. 检查每个 Agent
                    var currentTick = AgentOrchestrator.GameTick;
                    foreach (var config in AgentConfigs.All)
                    {
                        if (config.Name == "combat") continue;
                        if (!AgentOrchestrator.IsSleeping(config.Name)) continue;

                        bool shouldWake = config.IntervalGameHours > 0
                            && Scheduler.ShouldWake(config.Name, config.IntervalGameHours, currentTick);
                        if (config.TriggerDaily && AgentOrchestrator.IsNewDay(config.Name))
                            shouldWake = true;

                        if (shouldWake)
                        {
                            AgentOrchestrator.BeginAgent(config.Name);
                            Console.WriteLine($"=== {config.Name} 唤醒 (Load={Scheduler.LoadScore}, {Scheduler.Mode}) ===");

                            var prompt = await ctx.BuildAsync(config);
                            Console.WriteLine($"[Prompt] {prompt.Length} 字符");

                            // 通过 CCB WebSocket 发送给 Claude
                            await RunAgentViaCcb(config, prompt, mcp);

                            AgentOrchestrator.EndAgent(config.Name);
                            Console.WriteLine($"=== {config.Name} 休眠 ===");
                        }
                    }

                    // 3. Combat Agent — 有急迫事件时唤醒
                    if (AgentOrchestrator.IsSleeping("combat") && AgentOrchestrator.HasPendingEvents("combat"))
                    {
                        AgentOrchestrator.BeginAgent("combat");
                        Console.WriteLine("=== Combat 唤醒 ===");
                        var cc = AgentConfigs.Combat;
                        var cp = await ctx.BuildAsync(cc);
                        await RunAgentViaCcb(cc, cp, mcp);
                        AgentOrchestrator.EndAgent("combat");
                        Console.WriteLine("=== Combat 休眠 ===");
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

            ccbWs.OnAssistantText += text =>
            {
                if (!string.IsNullOrEmpty(text))
                    Console.WriteLine($"  [{config.Name}] {text.Substring(0, Math.Min(120, text.Length))}...");
            };

            if (!await ccbWs.ConnectAsync()) { CoreLog.Error($"[{config.Name}] CCB 连接失败"); return; }

            await AgentLoop.RunSessionAsync(config, prompt, mcp, ccbWs);
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
