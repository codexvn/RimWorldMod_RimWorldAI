using System;
using System.IO;
using System.Threading;
using RimWorldMCP.MapRendering;
using RimWorldMCP.Mcp;
using RimWorldMCP.Skills;
using RimWorldMCP.Tools;
using RimWorldMCP.Transport;
using Verse;

namespace RimWorldMCP
{
    /// <summary>MCP 服务全局生命周期管理（独立于 Game/存档）</summary>
    public static class McpServiceManager
    {
        private static ITransport? _transport;
        private static CancellationTokenSource? _cts;

        public static McpServer? Server { get; private set; }
        public static ToolRegistry? ToolRegistry { get; private set; }
        public static SkillRegistry? SkillRegistry { get; private set; }
        public static bool IsRunning => _transport != null;

        private const int DefaultPort = 9877;
        private const string DefaultHost = "0.0.0.0";

        /// <summary>启动 MCP 传输层与协议服务器（仅一次）</summary>
        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                // 1. 加载 SkillRegistry
                var skillsDir = FindSkillsDirectory();
                var skillRegistry = new SkillRegistry();
                skillRegistry.LoadFromDirectory(skillsDir);
                SkillRegistry = skillRegistry;

                // 2. 从 ModSettings 加载 OSS 配置
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);

                // 3. 初始化符号字典
                SymbolDictionary.Initialize();

                // 4. 创建 ToolRegistry + 注册所有 Tool
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry, skillRegistry);
                ToolRegistry = toolRegistry;

                // 5. 注册 Skill 资源
                foreach (var skill in skillRegistry.GetAll())
                {
                    toolRegistry.RegisterResource(
                        $"skill://{skill.Name}", skill.Name, skill.Description);
                }

                // 6. 创建 Transport（SSE + Streamable HTTP）
                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                var transport = new SseTransport(port, host);

                // 7. 创建 McpServer + 注入 /mcp 同步处理器
                var server = new McpServer(transport, toolRegistry);
                ((SseTransport)transport).SetMcpHandler(rawJson =>
                    server.ProcessMessageSync(rawJson));
                Server = server;

                // 8. 启动 Transport
                _cts = new CancellationTokenSource();
                transport.StartAsync(_cts.Token);
                _transport = transport;

                McpLog.Info($"MCP 服务已启动（主菜单）: http://{host}:{port}");
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

        /// <summary>停止 MCP 传输层</summary>
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
            SkillRegistry = null;
        }

        /// <summary>重新注册所有 Tool（游戏状态变化后刷新 Tool 实例）</summary>
        public static void RefreshTools()
        {
            if (ToolRegistry == null) return;

            var skillRegistry = SkillRegistry ?? new SkillRegistry();
            RegisterAllTools(ToolRegistry, skillRegistry);
            McpLog.Info("Tool 注册表已刷新");
        }

        private static void RegisterAllTools(ToolRegistry registry, SkillRegistry skillRegistry)
        {
            foreach (var type in typeof(ToolRegistry).Assembly.GetTypes())
            {
                if (!typeof(ITool).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
                    continue;

                McpLog.Info($"动态注册工具: {type.Name}");
                try
                {
                    ITool? tool;
                    var ctorWithSkill = type.GetConstructor(new[] { typeof(SkillRegistry) });
                    if (ctorWithSkill != null)
                        tool = (ITool)ctorWithSkill.Invoke(new object[] { skillRegistry });
                    else
                        tool = (ITool)Activator.CreateInstance(type);

                    if (tool != null)
                        registry.Register(tool);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"注册失败 {type.Name}: {ex.Message}");
                }
            }
        }

        internal static string FindSkillsDirectory()
        {
            try
            {
                var modRoot = TryGetModRootDir();
                if (modRoot != null)
                {
                    var skillsDir = Path.Combine(modRoot, "Skills");
                    McpLog.Info($"[skills] Mod.Content.RootDir Skills 路径 = {skillsDir} (Exists={Directory.Exists(skillsDir)})");
                    if (Directory.Exists(skillsDir))
                        return skillsDir;
                }

                var asmPath = typeof(McpServiceManager).Assembly.Location;
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var asmDir = Path.GetDirectoryName(asmPath);
                    if (asmDir != null)
                    {
                        var asmModRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                        var skillsDir = Path.Combine(asmModRoot, "Skills");
                        McpLog.Info($"[skills] Assembly.Location Skills 路径 = {skillsDir} (Exists={Directory.Exists(skillsDir)})");
                        if (Directory.Exists(skillsDir))
                            return skillsDir;
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[skills] 查找 Skills 目录异常: {ex.Message}");
            }

            var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            McpLog.Info($"[skills] 后备 Skills 路径 = {fallback} (Exists={Directory.Exists(fallback)})");
            if (Directory.Exists(fallback)) return fallback;
            McpLog.Warn("[skills] 所有路径均未找到 Skills 目录，返回相对路径 'Skills'");
            return "Skills";
        }

        private static string? TryGetModRootDir()
        {
            try
            {
                var content = RimWorldMCPMod.Instance?.Content;
                if (content != null && !string.IsNullOrEmpty(content.RootDir))
                    return content.RootDir;
            }
            catch { }
            return null;
        }
    }
}
