using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorldAgent.Core;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
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

            var mcpUrl = "http://10.126.126.1:9877/";
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
                else if ((arg == "--mcp-url" || arg == "--mcp" || arg == "-u") && i + 1 < args.Length)
                    mcpUrl = args[++i];
                else if (arg.StartsWith("--mcp-url="))
                    mcpUrl = arg.Substring("--mcp-url=".Length);
                else if (arg.StartsWith("--mcp="))
                    mcpUrl = arg.Substring("--mcp=".Length);
                else if (arg.StartsWith("-u="))
                    mcpUrl = arg.Substring("-u=".Length);
                else if (!arg.StartsWith("-"))
                    mcpUrl = arg;
            }
            if (!string.IsNullOrEmpty(modelName)) Console.WriteLine($"  模型: {modelName}");

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
                CcbDir = ccbDir ?? "",
                WaitForGame = true,
            };

            var engine = new AgentEngine(cfg, dbStore, gameState,
                logInfo: msg => Console.WriteLine($"[Core] {msg}"),
                logError: msg => Console.Error.WriteLine($"[Core] {msg}"),
                logDebug: msg => Console.WriteLine($"[Core] {msg}"),
                logWarn: msg => Console.Error.WriteLine($"[Core] {msg}"));

            Console.WriteLine($"RimWorldAgent 启动");
            Console.WriteLine($"  MCP: {mcpUrl}");
            Console.WriteLine($"  Project: {projectPath}");
            Console.WriteLine("等待游戏启动...");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cts.Cancel(); };

            // 先启动 UIMessageBus + SQLite，确保 InitAsync 触发 Token 推送时 WS 已就绪
            var bridgePort = 19999;
            UIMessageBus.Start(port: bridgePort);
            Console.WriteLine($"[Core] UIMessageBus: ws://0.0.0.0:{bridgePort}");

            NativeResolver.Setup(AppDomain.CurrentDomain.BaseDirectory);
            AgentLoop.ConversationStore = new SqliteConversationStore(
                Path.Combine(projectPath, "conversation.db"), "exe-session");

            await engine.InitAsync();

            // CCB ↔ UIMessageBus 双向中继（SDK↔UiMessage 转换在 AgentCore）
            if (engine.CcbWs != null)
                AgentLoop.WireUIMessageBus(engine.CcbWs);

            // 初始化完成后显式推送一次预算状态
            UIMessageBus.PushUiMessage(UiMessage.BudgetStatus(
                TokenUsageTracker.TotalAllTokens, AgentLoop.BudgetLimit, "Idle",
                TokenUsageTracker.TotalCacheReadTokens, TokenUsageTracker.TotalInputTokens + TokenUsageTracker.TotalCacheReadTokens,
                TokenUsageTracker.TotalCacheCreateTokens, 0,
                TokenUsageTracker.CurrentInputTokens,
                TokenUsageTracker.CurrentCacheReadTokens, TokenUsageTracker.CurrentCacheCreateTokens));

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
            UIMessageBus.Stop();
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