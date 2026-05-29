using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleMspServer;
using SimpleMspServer.Mcp;

namespace SimpleMspServer.Tests.Cli
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var port =29878;
            if (args.Length > 0) int.TryParse(args[0], out port);

            Console.WriteLine($"SimpleMCP Demo Server");
            Console.WriteLine($"端口: {port}");
            Console.WriteLine($"MCP 端点: http://localhost:{port}/mcp");
            Console.WriteLine($"SSE: curl -N -H \"Mcp-Session-Id: <sid>\" http://localhost:{port}/mcp");
            Console.WriteLine($"按 Ctrl+C 退出");
            Console.WriteLine();

            var log = new DelegateMspLog(msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));
            var host = new McpServiceHost(port, "localhost", log);
            host.RegisterProvider(new DemoProvider());
            host.Start();

            // 每 5 秒推送测试事件
            int counter = 0;
            var timer = new System.Threading.Timer(_ =>
            {
                counter++;
                var now = DateTime.Now.ToString("HH:mm:ss");
                var json = JsonSerializer.Serialize(new
                {
                    type = "event",
                    category = "Test",
                    severity = "Info",
                    summary = $"测试事件 #{counter}",
                    time = now
                });
                host.SendEvent(json);
                Console.WriteLine($"  [event] 已发送 #{counter} ({now})");
            }, null, 5000, 5000);

            // 阻塞主线程直到 Ctrl+C
            var done = new System.Threading.ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            done.Wait();

            timer.Dispose();
            Console.WriteLine("\n正在停止...");
            host.Stop();
            Console.WriteLine("已退出。");
        }
    }

    // ===== Demo IToolProvider =====

    class DemoProvider : IToolProvider
    {
        string IToolProvider.ProviderName => "DemoProvider";

        List<ToolDefinition> IToolProvider.GetDefinitions() => new()
        {
            new ToolDefinition
            {
                Name = "hello",
                Description = "返回问候语",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new {
                        name = new { type = "string", description = "名称" }
                    }
                })
            },
            new ToolDefinition
            {
                Name = "echo",
                Description = "回显消息",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new {
                        message = new { type = "string", description = "要回显的消息" }
                    }
                })
            }
        };

        Task<ToolCallResult> IToolProvider.ExecuteAsync(string name, JsonElement? args)
        {
            return Task.FromResult(name switch
            {
                "hello" => new ToolCallResult
                {
                    Content = new List<ContentItem>
                    {
                        new() { Text = $"Hello, {GetArg(args, "name", "World")}!" }
                    }
                },
                "echo" => new ToolCallResult
                {
                    Content = new List<ContentItem>
                    {
                        new() { Text = GetArg(args, "message", "(空)") }
                    }
                },
                _ => new ToolCallResult
                {
                    IsError = true,
                    Content = new List<ContentItem> { new() { Text = $"Unknown tool: {name}" } }
                }
            });
        }

        List<ResourceDefinition> IToolProvider.GetResources() => new();
        string? IToolProvider.ReadResource(string uri) => null;

        static string GetArg(JsonElement? args, string key, string fallback)
        {
            if (args == null) return fallback;
            if (args.Value.TryGetProperty(key, out var v)) return v.GetString() ?? fallback;
            return fallback;
        }
    }
}
