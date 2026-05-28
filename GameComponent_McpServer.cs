using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldMCP.Harmony;
using RimWorldMCP.Mcp;
using RimWorldMCP.Skills;
using RimWorldMCP.Tools;
using RimWorldMCP.Transport;
using Verse;

namespace RimWorldMCP
{
    public class GameComponent_McpServer : GameComponent
    {
        private ITransport? _transport;
        private CancellationTokenSource? _cts;
        private static ITransport? s_activeTransport;
        /// <summary>Skill 注册表 — Agent 读取用于晨报</summary>
        public static SkillRegistry? SkillRegistry => s_skillRegistry;
        internal static SkillRegistry? s_skillRegistry;

        /// <summary>当前会话 ID — 由存档 ExposeData 持久化，供 Agent 读取</summary>
        public static string CurrentSessionId { get; set; } = "";

        private const int DefaultPort = 9877;
        private const string DefaultHost = "0.0.0.0";
        private static int _lastEventPushTick;

        // 弹框扫描跟踪
        private static int _lastDialogCount;
        private static string _lastDialogKey = "";

        public GameComponent_McpServer(Game game)
        {
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            DeteriorationTracker.Reset();
            CurrentSessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            StartMcpService();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            DeteriorationTracker.Reset();
            if (string.IsNullOrEmpty(CurrentSessionId))
                CurrentSessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            StartMcpService();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
                _sessionIdBacking = CurrentSessionId;
            Scribe_Values.Look(ref _sessionIdBacking, "sessionId", "");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                CurrentSessionId = _sessionIdBacking;
        }

        private static string _sessionIdBacking = "";

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();

            McpLog.Flush();
            McpCommandQueue.ProcessPending();
            Tool_AdvanceTick.ProcessPending();
            Tool_AdvanceTick.LowSpeedTick();
            PushGameEventsToSse();
            McpOssUploader.ProcessPendingUploads();
            McpCommandQueue.ProcessDeferredCleanup();

            CameraHelper.AutoTrackColonistsTick();
        }

        /// <summary>从 NotificationBus 取出游戏事件，通过 SSE 推送给所有客户端</summary>
        private static void PushGameEventsToSse()
        {
            if (!NotificationBus.Pending.IsEmpty)
            {
                var events = NotificationBus.Drain();
                if (events.Count == 0) return;

                bool hasCritical = false;
                int nonCritical = 0;

                foreach (var e in events)
                {
                    var level = NotificationBus.GetEventLevel(e.Type, e.DangerLabel);
                    if (level == EventLevel.Silent) continue;

                    if (level == EventLevel.Critical)
                        hasCritical = true;
                    else
                        nonCritical++;

                    var json = JsonSerializer.Serialize(new
                    {
                        type = "game_event",
                        level = level.ToString(),
                        letterType = e.Type.ToString(),
                        label = e.Label,
                        text = e.Text,
                        dangerLabel = e.DangerLabel,
                        priority = e.Priority
                    });
                    _ = SseTransport.BroadcastEvent("game_event", json);
                }

                if (hasCritical)
                {
                    int high = 0, warn = 0;
                    foreach (var n in events)
                    {
                        if (NotificationBus.IsHighDanger(n.Type, n.DangerLabel, n.Priority))
                            high++;
                        else if (n.Type == NotificationType.Letter || n.Type == NotificationType.Message)
                            warn++;
                    }
                    var parts = new System.Collections.Generic.List<string>(3);
                    if (high > 0) parts.Add($"\U0001f534x{high}");
                    if (warn > 0) parts.Add($"\U0001f7e1x{warn}");
                    if (parts.Count == 0) parts.Add($"ℹ️x{events.Count}");
                    McpInternalState.DangerSummary = $"待处理: {string.Join(" ", parts)}";
                    McpInternalState.DangerPaused = true;
                }
                else
                {
                    // 此批无 L3 事件，清除危险暂停状态
                    McpInternalState.DangerPaused = false;
                    McpInternalState.DangerSummary = "";
                }

                if (nonCritical > 0)
                    McpInternalState.PendingLevel12Count += nonCritical;
            }

            // 周期性检查物品腐坏，推送 SSE 事件
            CheckDeteriorationAndPushSse();

            // 周期性检查弹框变化，推送 SSE 事件
            CheckDialogsAndPushSse();
        }

        /// <summary>周期性检查物品腐坏/耐久降低，通过 SSE 推送</summary>
        private static void CheckDeteriorationAndPushSse()
        {
            var map = Find.CurrentMap;
            if (map == null) return;

            var result = DeteriorationTracker.CheckAndNotify(map);
            if (result != null)
            {
                var json = JsonSerializer.Serialize(new { text = result });
                _ = SseTransport.BroadcastEvent("deterioration_warning", json);
            }
        }

        /// <summary>检查弹框变化，通过 SSE 推送（避免每帧重复广播）</summary>
        private static void CheckDialogsAndPushSse()
        {
            var dialogs = Helpers.DialogHelper.GetInteractableDialogs();
            int count = dialogs.Count;
            string key = count > 0 ? string.Join("|", dialogs.Select(w => w.GetType().Name)) : "";

            if (count == _lastDialogCount && key == _lastDialogKey) return;

            _lastDialogCount = count;
            _lastDialogKey = key;

            var json = JsonSerializer.Serialize(new
            {
                count,
                dialogs = dialogs.ConvertAll(w => new { type = w.GetType().Name })
            });
            _ = SseTransport.BroadcastEvent("dialog_state", json);
        }

        private void StartMcpService()
        {
            StopMcpService();

            try
            {
                // 1. 加载 SkillRegistry
                var skillsDir = FindSkillsDirectory();
                var skillRegistry = new SkillRegistry();
                skillRegistry.LoadFromDirectory(skillsDir);
                s_skillRegistry = skillRegistry;

                // 1.5 从 ModSettings 加载 OSS 配置
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);

                // 2. 创建 ToolRegistry + 注册 Tool
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry, skillRegistry);

                // 3. 注册 Skill 资源
                foreach (var skill in skillRegistry.GetAll())
                {
                    toolRegistry.RegisterResource(
                        $"skill://{skill.Name}", skill.Name, skill.Description);
                }

                // 4. 创建 Transport
                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                var transport = new SseTransport(port, host);

                // 5. 创建 McpServer + 注入 /mcp 处理器
                var server = new McpServer(transport, toolRegistry);
                ((SseTransport)transport).SetMcpHandler(rawJson =>
                    server.ProcessMessageSync(rawJson));

                // 6. 启动 Transport
                _cts = new CancellationTokenSource();
                transport.StartAsync(_cts.Token);

                _transport = transport;
                s_activeTransport = transport;

                McpLog.Info($"MCP 服务已启动: http://{host}:{port}, 传输: http");
            }
            catch (Exception ex)
            {
                if (_cts != null)
                {
                    try { _cts.Cancel(); _cts.Dispose(); } catch { }
                    _cts = null;
                }
                McpLog.Error($"启动失败: {ex.Message}");
            }
        }

        private void StopMcpService()
        {
            if (_transport != null)
            {
                try { _transport.StopAsync(); } catch (Exception ex) { McpLog.Warn($"停止传输时出错: {ex.Message}"); }
                _transport = null;
            }

            if (s_activeTransport != null && s_activeTransport != _transport)
            {
                try { s_activeTransport.StopAsync(); } catch (Exception ex) { McpLog.Warn($"停止残留传输时出错: {ex.Message}"); }
                s_activeTransport = null;
            }

            if (_cts != null)
            {
                try { _cts.Cancel(); _cts.Dispose(); } catch { }
                _cts = null;
            }
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

        private static string FindSkillsDirectory()
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

                var asmPath = typeof(GameComponent_McpServer).Assembly.Location;
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
