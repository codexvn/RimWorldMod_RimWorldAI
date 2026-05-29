using System;
using System.IO;
using System.Threading;
using RimWorldMCP.MapRendering;
using SimpleMspServer.Mcp;
using RimWorldMCP.Tools;
using SimpleMspServer.Transport;
using Verse;

namespace RimWorldMCP
{
    public static class McpServiceManager
    {
        private static ITransport? _transport;
        private static CancellationTokenSource? _cts;

        public static McpServer? Server { get; private set; }
        public static ToolRegistry? ToolRegistry { get; private set; }
        public static bool IsRunning => _transport != null;

        private const int DefaultPort = 9877;
        private const string DefaultHost = "0.0.0.0";

        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                // 1. 从 ModSettings 加载 OSS 配置
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);

                // 2. 初始化符号字典
                SymbolDictionary.Initialize();

                // 3. 创建 ToolRegistry + 注册所有 Tool
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry);
                ToolRegistry = toolRegistry;

                // 4. 创建 Transport
                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                var transport = new SseTransport(port, host);

                // 5. 创建 McpServer
                var server = new McpServer(transport, toolRegistry);
                ((SseTransport)transport).SetMcpHandler(rawJson =>
                    server.ProcessMessageSync(rawJson));
                Server = server;

                // 6. 启动 Transport
                _cts = new CancellationTokenSource();
                transport.StartAsync(_cts.Token);
                _transport = transport;

                McpLog.Info($"MCP 服务已启动: http://{host}:{port}");
            }
            catch (Exception ex)
            {
                if (_cts != null)
                {
                    try { _cts.Cancel(); _cts.Dispose(); } catch { }
                    _cts = null;
                }
                _transport = null;
                McpLog.Error($"MCP 服务启动失败: {ex.Message}");
            }
        }

        public static void Stop()
        {
            if (_transport != null)
            {
                try { _transport.StopAsync(); } catch (Exception ex) { McpLog.Warn($"停止传输时出错: {ex.Message}"); }
                _transport = null;
            }

            if (_cts != null)
            {
                try { _cts.Cancel(); _cts.Dispose(); } catch { }
                _cts = null;
            }

            Server = null;
            ToolRegistry = null;
        }

        public static void RefreshTools()
        {
            if (ToolRegistry == null) return;
            RegisterAllTools(ToolRegistry);
            McpLog.Info("Tool 注册表已刷新");
        }

        private static void RegisterAllTools(ToolRegistry registry)
        {
            foreach (var type in typeof(ToolRegistry).Assembly.GetTypes())
            {
                if (!typeof(ITool).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
                    continue;

                McpLog.Info($"注册工具: {type.Name}");
                try
                {
                    var tool = (ITool)Activator.CreateInstance(type);

                    if (tool != null)
                    {
                        if (tool is IHasAvailability hasAvail && !hasAvail.IsAvailable)
                        {
                            McpLog.Info($"跳过不可用工具: {type.Name}");
                            continue;
                        }
                        registry.Register(tool);
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"注册失败 {type.Name}: {ex.Message}");
                }
            }
        }
    }
}
