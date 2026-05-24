using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        private string _sessionId = "";
        private const int DefaultPort = 9877;
        private const string DefaultHost = "0.0.0.0";

        public GameComponent_McpServer(Game game)
        {
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            _sessionId = "rimworld-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            StartMcpService();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            StartMcpService();
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            McpLog.Flush();
            McpCommandQueue.ProcessPending();
            BridgeLifecycle.Tick();
            McpOssUploader.ProcessPendingUploads();
            McpCommandQueue.ProcessDeferredCleanup();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _sessionId, "mcpSessionId", "");
            // 旧存档没有此字段时自动生成
            if (Scribe.mode == LoadSaveMode.LoadingVars && string.IsNullOrEmpty(_sessionId))
                _sessionId = "rimworld-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            // 如果 ExposeData 在 StartMcpService 之后才还原 _sessionId，在这里补同步
            if (!string.IsNullOrEmpty(_sessionId) && GatewayClient.SessionId != _sessionId)
            {
                GatewayClient.SessionId = _sessionId;
                McpLog.Info($"[session] FinalizeInit 同步 ID = {_sessionId}");
            }
        }

        private void StartMcpService()
        {
            // 先清理上一 Game 实例可能遗留的监听器（RimWorld 返回主菜单时 Game.Dispose 不通知 GameComponent）
            StopMcpService();

            try
            {
                // 1. 加载 SkillRegistry
                var skillsDir = FindSkillsDirectory();
                var skillRegistry = new SkillRegistry();
                skillRegistry.LoadFromDirectory(skillsDir);

                // 1.5 从 ModSettings 加载 OSS 配置
                if (RimWorldMCPMod.Instance != null)
                    McpOssConfig.LoadFromModSettings(RimWorldMCPMod.Instance.Settings);

                // 2. 创建 ToolRegistry + 注册 24 个 Tool
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry, skillRegistry);

                // 3. 注册 Skill 资源
                foreach (var skill in skillRegistry.GetAll())
                {
                    toolRegistry.RegisterResource(
                        $"skill://{skill.Name}", skill.Name, skill.Description);
                }

                // 4. 创建 Transport（SSE + Streamable HTTP）
                var host = RimWorldMCPMod.Instance?.Settings?.McpHost ?? DefaultHost;
                var port = RimWorldMCPMod.Instance?.Settings?.McpPort ?? DefaultPort;
                var transport = new SseTransport(port, host);

                // 5. 创建 McpServer + 注入 /mcp 同步处理器
                var server = new McpServer(transport, toolRegistry);
                ((SseTransport)transport).SetMcpHandler(rawJson =>
                    server.ProcessMessageSync(rawJson));

                // 6. 启动 Transport（成功后才赋值 _transport，确保失败时下次可重试）
                _cts = new CancellationTokenSource();
                transport.StartAsync(_cts.Token);

                _transport = transport;
                s_activeTransport = transport;

                McpLog.Info($"MCP 服务已启动: http://{host}:{port}, 传输: http");

                // 同步存档会话 ID 到 Gateway
                GatewayClient.SessionId = _sessionId;
                McpLog.Info($"[session] ID = {_sessionId}");

                // 新游戏/加载游戏时重置事件监控的已见 Letter 列表
                GatewayEventMonitor.Reset();

                // 启动桥接器（独立于 MCP Server）
                _ = BridgeLifecycle.StartAsync();
            }
            catch (Exception ex)
            {
                // 启动失败时清理 _cts，_transport 保持 null 以便下次重试
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
            registry.Register(new Tool_GetGameContext());
            registry.Register(new Tool_GetResources());
            registry.Register(new Tool_ListRecipes());
            registry.Register(new Tool_CreateBill());
            registry.Register(new Tool_GetBills());
            registry.Register(new Tool_ManageBill());
            registry.Register(new Tool_DesignateBuild());
            registry.Register(new Tool_DesignateRoom());
            registry.Register(new Tool_DesignatePlantsCut());
            registry.Register(new Tool_DesignateMine());
            registry.Register(new Tool_DesignateDeconstruct());
            registry.Register(new Tool_DesignateClearPlants());
            registry.Register(new Tool_DesignateHarvest());
            registry.Register(new Tool_TakeScreenshot());
            registry.Register(new Tool_ListResearchProjects());
            registry.Register(new Tool_GetResearchProgress());
            registry.Register(new Tool_SetResearchProject());
            registry.Register(new Tool_GetColonists());
            registry.Register(new Tool_GetColonistNeeds());
            registry.Register(new Tool_SetWorkPriority());
            registry.Register(new Tool_GetColonistHealth());
            registry.Register(new Tool_ScheduleOperation());
            registry.Register(new Tool_ForceEquip());
            registry.Register(new Tool_EquipPawn());
            registry.Register(new Tool_DraftPawn());
            registry.Register(new Tool_GetDefenseStatus());
            registry.Register(new Tool_GetTileGrid());
            registry.Register(new Tool_GetTileDetail());
            registry.Register(new Tool_CheckColony());
            registry.Register(new Tool_GetSkills(skillRegistry));
            registry.Register(new Tool_ActiveSkill(skillRegistry));
            registry.Register(new Tool_AllowAllItems());
            registry.Register(new Tool_PickUpItem());
            registry.Register(new Tool_DropEquipment());
            registry.Register(new Tool_StripPawn());
            registry.Register(new Tool_ArrestPawn());
            registry.Register(new Tool_RescuePawn());
            registry.Register(new Tool_CapturePawn());
            registry.Register(new Tool_IngestItem());
            registry.Register(new Tool_ForceDress());
            registry.Register(new Tool_CreateStockpile());
            registry.Register(new Tool_SearchMap());
            registry.Register(new Tool_FindPawn());
            registry.Register(new Tool_GetThingDef());
            registry.Register(new Tool_SearchThingDef());
            registry.Register(new Tool_MovePawn());
            registry.Register(new Tool_UninstallBuilding());
            registry.Register(new Tool_InstallMinifiedThing());
            registry.Register(new Tool_FindEnemies());
            registry.Register(new Tool_AttackPawn());
            registry.Register(new Tool_ForceAttack());
        }

        private static string FindSkillsDirectory()
        {
            try
            {
                // Assembly 路径: .../Mods/RimWorldMCP/1.6/Assemblies/RimWorldMCP.dll
                // Skills 路径: .../Mods/RimWorldMCP/Skills/
                var asmPath = typeof(GameComponent_McpServer).Assembly.Location;
                McpLog.Info($"[skills] Assembly.Location = {asmPath ?? "null"}");
                if (string.IsNullOrEmpty(asmPath))
                {
                    McpLog.Warn("[skills] Assembly.Location 为空，使用后备路径");
                    return FallbackSkillsDir();
                }

                var asmDir = Path.GetDirectoryName(asmPath);
                McpLog.Info($"[skills] Assembly 目录 = {asmDir ?? "null"}");
                if (asmDir == null)
                {
                    McpLog.Warn("[skills] GetDirectoryName 返回 null，使用后备路径");
                    return FallbackSkillsDir();
                }

                // 从 Assemblies/ 向上两级到 mod 根目录
                var modRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                McpLog.Info($"[skills] modRoot (asm/../..) = {modRoot}");

                var skillsDir = Path.Combine(modRoot, "Skills");
                McpLog.Info($"[skills] primary Skills 路径 = {skillsDir} (Exists={Directory.Exists(skillsDir)})");
                if (Directory.Exists(skillsDir))
                    return skillsDir;

                // 备选：直接在 Skills/ 找（开发模式可能放这里）
                var altDir = Path.Combine(modRoot, "..", "Skills");
                McpLog.Info($"[skills] alt Skills 路径 = {altDir} (Exists={Directory.Exists(altDir)})");
                if (Directory.Exists(altDir))
                    return altDir;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[skills] 查找 Skills 目录异常: {ex.Message}");
            }

            McpLog.Warn("[skills] 所有路径均未找到 Skills 目录，使用后备路径");
            return FallbackSkillsDir();
        }

        private static string FallbackSkillsDir()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            McpLog.Info($"[skills] FallbackSkillsDir = {dir} (Exists={Directory.Exists(dir)})");
            if (Directory.Exists(dir)) return dir;
            McpLog.Warn("[skills] 后备路径也不存在，返回相对路径 'Skills'");
            return "Skills";
        }

    }
}
