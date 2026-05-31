using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.Data;
using RimWorldAgent.Core.Mcp;

namespace RimWorldAgent
{
    internal class Program
    {
        private static readonly CancellationTokenSource _cts = new();

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var mcpUrl = "http://localhost:9877";
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
            if (!string.IsNullOrEmpty(modelName)) Console.WriteLine($"  模型: {modelName}");
            if (planSpeed != "paused") Console.WriteLine($"  Plan 速度: {planSpeed}");

            var projectPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "claude-sessions", "dev-session"));
            Directory.CreateDirectory(projectPath);

            var dbStore = new JsonDbStore(Path.Combine(projectPath, "RimWorldMCP_Token.json"));

            var mcpClient = new McpClient(mcpUrl);
            var gameState = new RemoteGameStateProvider(mcpClient);

            var ccbDir = FindCcbDir();

            var cfg = new AgentEngineConfig
            {
                ProjectPath = projectPath,
                McpUrl = mcpUrl,
                ModelName = modelName,
                PlanSpeed = planSpeed,
                CcbDir = ccbDir ?? "",
                WaitForGame = true,
            };

            var engine = new AgentEngine(cfg, dbStore, gameState,
                logInfo: msg => Console.WriteLine($"[Core] {msg}"),
                logError: msg => Console.Error.WriteLine($"[Core] {msg}"),
                logDebug: msg => Console.WriteLine($"[Core] {msg}"));

            Console.WriteLine($"RimWorldAgent 启动");
            Console.WriteLine($"  MCP: {mcpUrl}");
            Console.WriteLine($"  Project: {projectPath}");
            Console.WriteLine("等待游戏启动...");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };

            await engine.InitAsync();
            Console.WriteLine("Agent Main Loop 启动 (Ctrl+C 退出)");

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    engine.Tick();
                    await engine.TickAsync();
                    await Task.Delay(2000, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }

            Console.WriteLine("RimWorldAgent 退出");
            engine.Dispose();
        }

        private static string? FindCcbDir()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var pub = Path.GetFullPath(Path.Combine(baseDir, "cc-companion"));
            var src = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "cc-companion"));
            if (Directory.Exists(pub) && File.Exists(Path.Combine(pub, "package.json"))) return pub;
            if (Directory.Exists(src) && File.Exists(Path.Combine(src, "package.json"))) return src;
            return null;
        }
    }
}
