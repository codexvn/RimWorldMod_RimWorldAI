using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using RimWorldMCP.MapRendering;
using RimWorldMCP.Tools;
using UnityEngine;

namespace RimWorldMCP
{
    public static class McpServiceManager
    {
        private static SimpleMspServer.McpServiceHost? _host;
        private static int _shutdownHooksRegistered;
        private static int _stopping;

        public static ToolRegistry? ToolRegistry { get; private set; }
        public static SimpleMspServer.McpServiceHost? Host => _host;
        public static bool IsRunning => _host?.IsRunning ?? false;

        private const int DefaultPort = 9877;
        private const string DefaultHost = "0.0.0.0";

        public static void Start()
        {
            EnsureShutdownHooksRegistered();
            if (IsRunning) return;

            try
            {
                McpLog.Info("Step 1/5: OSS 配置 + 符号字典...");
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);
                SymbolDictionary.Initialize();

                McpLog.Info("Step 2/5: 创建 ToolRegistry + 注册全部 Tool...");
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry);
                ToolRegistry = toolRegistry;

                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                McpLog.Info($"Step 4/5: 创建 McpServiceHost + 注册 ToolRegistry IToolProvider (host={host}, port={port})...");
                _host?.Dispose();
                _host = new SimpleMspServer.McpServiceHost(port, host,
                    new SimpleMspServer.DelegateMspLog(McpLog.Info));
                _host.RegisterProvider(toolRegistry);

                McpLog.Info("Step 5/5: 启动 HTTP 监听...");
                _host.Start();

                if (_host.IsRunning)
                    McpLog.Info($"MCP 服务已启动: http://{host}:{port}");
                else
                {
                    _host.Dispose();
                    _host = null;
                    McpLog.Error($"MCP 服务启动失败: 端口 {port} 未能成功监听（可能被占用或残留僵尸监听）");
                }
            }
            catch (Exception ex)
            {
                _host?.Dispose(); _host = null;
                McpLog.Error($"MCP 服务启动失败: {ex}");
            }
        }

        public static void Stop()
        {
            // 进程退出路径可能并发触发多个钩子，保证只清理一次
            if (Interlocked.Exchange(ref _stopping, 1) == 1)
                return;

            try
            {
                if (_host == null)
                    return;

                McpLog.Info("正在停止 MCP 服务并释放端口...");
                _host.Dispose();
                McpLog.Info("MCP 服务已停止，端口已释放");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"停止 MCP 服务异常: {ex.Message}");
            }
            finally
            {
                _host = null;
                ToolRegistry = null;
                Interlocked.Exchange(ref _stopping, 0);
            }
        }

        public static void RefreshTools()
        {
            if (ToolRegistry == null) return;
            RegisterAllTools(ToolRegistry);
            McpLog.Info("Tool 注册表已刷新");
        }

        private static void EnsureShutdownHooksRegistered()
        {
            if (Interlocked.Exchange(ref _shutdownHooksRegistered, 1) == 1)
                return;

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, __) => StopFromExitHook("ProcessExit");
                AppDomain.CurrentDomain.DomainUnload += (_, __) => StopFromExitHook("DomainUnload");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"注册 AppDomain 退出钩子失败: {ex.Message}");
            }

            try
            {
                Application.quitting += () => StopFromExitHook("Application.quitting");
            }
            catch (Exception ex)
            {
                McpLog.Warn($"注册 Application.quitting 钩子失败: {ex.Message}");
            }

            McpLog.Info("已注册 MCP 退出释放钩子 (ProcessExit/DomainUnload/Application.quitting)");
        }

        private static void StopFromExitHook(string source)
        {
            try
            {
                // 退出阶段尽量少依赖游戏日志系统；失败也不要抛出
                if (_host != null)
                    Console.Error.WriteLine($"[RimWorldMCP] {source}: 释放 MCP HttpListener 端口...");
                Stop();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RimWorldMCP] {source}: 释放端口失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RegisterAllTools(ToolRegistry registry)
        {
            try
            {
                var asm = typeof(ToolRegistry).Assembly;
                McpLog.Info($"扫描程序集: {asm.FullName} ({asm.Location})");

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                    McpLog.Info($"GetTypes() 返回 {types.Length} 个类型");
                }
                catch (ReflectionTypeLoadException ex)
                {
                    McpLog.Error($"GetTypes() 失败: {ex}");
                    foreach (var le in ex.LoaderExceptions)
                    {
                        if (le != null) McpLog.Error($"  LoaderException: {le}");
                    }
                    return;
                }

                foreach (var type in types)
                {
                    if (type.IsInterface || type.IsAbstract)
                        continue;
                    if (!typeof(ITool).IsAssignableFrom(type))
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
                    catch (Exception ex) { McpLog.Warn($"注册失败 {type.Name}: {ex}"); }
                }

                McpLog.Info($"共注册 {registry.AllTools.Count} 个工具");
            }
            catch (Exception ex)
            {
                McpLog.Error($"RegisterAllTools 异常: {ex}");
            }
        }
    }
}
