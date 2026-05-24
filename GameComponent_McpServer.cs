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
        private const int DefaultPort = 9877;

        public GameComponent_McpServer(Game game)
        {
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
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
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
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

                // 2. 创建 ToolRegistry + 注册 21 个 Tool
                var toolRegistry = new ToolRegistry();
                RegisterAllTools(toolRegistry, skillRegistry);

                // 3. 注册 Skill 资源
                foreach (var skill in skillRegistry.GetAll())
                {
                    toolRegistry.RegisterResource(
                        $"skill://{skill.Name}", skill.Name, skill.Description);
                }

                // 4. 创建 Transport（默认 SSE + Streamable HTTP，端口 9877）
                var transport = new SseTransport(DefaultPort);

                // 5. 创建 McpServer + 注入 /mcp 同步处理器
                var server = new McpServer(transport, toolRegistry);
                ((SseTransport)transport).SetMcpHandler(rawJson =>
                    server.ProcessMessageSync(rawJson));

                // 6. 启动 Transport（成功后才赋值 _transport，确保失败时下次可重试）
                _cts = new CancellationTokenSource();
                transport.StartAsync(_cts.Token);

                _transport = transport;
                s_activeTransport = transport;

                McpLog.Info($"MCP 服务已启动，端口: {DefaultPort}, 传输: http");
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
            registry.Register(new Tool_ListResearchProjects());
            registry.Register(new Tool_GetResearchProgress());
            registry.Register(new Tool_SetResearchProject());
            registry.Register(new Tool_GetColonists());
            registry.Register(new Tool_GetColonistNeeds());
            registry.Register(new Tool_SetWorkPriority());
            registry.Register(new Tool_GetColonistHealth());
            registry.Register(new Tool_ScheduleOperation());
            registry.Register(new Tool_EquipPawn());
            registry.Register(new Tool_DraftPawn());
            registry.Register(new Tool_GetDefenseStatus());
            registry.Register(new Tool_GetSkills(skillRegistry));
            registry.Register(new Tool_ActiveSkill(skillRegistry));
        }

        private static string FindSkillsDirectory()
        {
            try
            {
                // Assembly 路径: .../Mods/RimWorldMCP/1.6/Assemblies/RimWorldMCP.dll
                // Skills 路径: .../Mods/RimWorldMCP/Skills/
                var asmPath = typeof(GameComponent_McpServer).Assembly.Location;
                if (string.IsNullOrEmpty(asmPath))
                    return FallbackSkillsDir();

                var asmDir = Path.GetDirectoryName(asmPath);
                if (asmDir == null)
                    return FallbackSkillsDir();

                // 从 Assemblies/ 向上两级到 mod 根目录
                var modRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                var skillsDir = Path.Combine(modRoot, "Skills");

                McpLog.Info($"尝试 Skills 路径: {skillsDir}");
                if (Directory.Exists(skillsDir))
                    return skillsDir;

                // 备选：直接在 Skills/ 找（开发模式可能放这里）
                var altDir = Path.Combine(modRoot, "..", "Skills");
                if (Directory.Exists(altDir))
                    return altDir;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"查找 Skills 目录异常: {ex.Message}");
            }

            return FallbackSkillsDir();
        }

        private static string FallbackSkillsDir()
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Skills");
            if (Directory.Exists(dir)) return dir;
            return "Skills";
        }
    }
}
