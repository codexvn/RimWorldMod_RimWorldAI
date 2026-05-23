using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RimWorldMCP.Skills;
using RimWorldMCP.Tools;
using RimWorldMCP.Transport;
using Verse;

namespace RimWorldMCP
{
    public class GameComponent_McpServer : GameComponent
    {
        private StreamableHttpServerTransport? _transport;
        private McpServer? _server;
        private ToolRegistry? _toolRegistry;
        private CancellationTokenSource? _cts;
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
            McpCommandQueue.ProcessPending();
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }

        private void StartMcpService()
        {
            if (_transport != null) return;

            try
            {
                var skillsDir = FindSkillsDirectory();
                var skillRegistry = new SkillRegistry();
                skillRegistry.LoadFromDirectory(skillsDir);

                _toolRegistry = new ToolRegistry();
                RegisterAllTools(_toolRegistry, skillRegistry);

                foreach (var skill in skillRegistry.GetAll())
                    _toolRegistry.RegisterResource($"skill://{skill.Name}", skill.Name, skill.Description);

                // 创建 SDK StreamableHttpTransport
                _transport = new StreamableHttpServerTransport();

                // 创建 McpServerOptions + 配置 handlers
                var registry = _toolRegistry;
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "RimWorld MCP", Version = "1.0.0" },
                    ProtocolVersion = "2024-11-05",
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = (ctx, ct) =>
                        {
                            var tools = registry.GetDefinitions().Select(t => new ModelContextProtocol.Protocol.Tool
                            {
                                Name = t.Name,
                                Description = t.Description,
                                InputSchema = t.InputSchema
                            }).ToList();
                            return new ValueTask<ListToolsResult>(new ListToolsResult { Tools = tools });
                        },
                        CallToolHandler = (ctx, ct) =>
                        {
                            // IDictionary<string, JsonElement> → JsonElement
                            JsonElement? args = null;
                            if (ctx.Params.Arguments != null)
                            {
                                args = JsonSerializer.SerializeToElement(ctx.Params.Arguments);
                            }
                            var result = registry.ExecuteAsync(ctx.Params.Name, args).GetAwaiter().GetResult();
                            return new ValueTask<CallToolResult>(new CallToolResult
                            {
                                Content = result.Content.Select(c => (ContentBlock)new TextContentBlock { Text = c.Text }).ToList(),
                                IsError = result.IsError
                            });
                        }
                    }
                };

                _server = McpServer.Create(_transport, options);

                // 启动 MCP 消息处理循环
                _cts = new CancellationTokenSource();
                _ = _server.RunAsync(_cts.Token);

                // 启动 HttpListener
                StartHttpListener(_transport, _cts.Token);

                McpLog.Info($"MCP 服务已启动，端口: {DefaultPort}, 传输: http");
            }
            catch (Exception ex)
            {
                McpLog.Error($"启动失败: {ex.Message}");
            }
        }

        private void StartHttpListener(StreamableHttpServerTransport transport, CancellationToken ct)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{DefaultPort}/");
            listener.Start();

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && listener.IsListening)
                {
                    try
                    {
                        var ctx = await listener.GetContextAsync();
                        _ = HandleHttpRequest(ctx, transport, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (HttpListenerException) { break; }
                    catch (Exception ex) { McpLog.Info($"[http] 接受连接错误: {ex.Message}"); }
                }
                listener.Stop();
                listener.Close();
            }, ct);
        }

        private async Task HandleHttpRequest(HttpListenerContext ctx, StreamableHttpServerTransport transport, CancellationToken ct)
        {
            var request = ctx.Request;
            var response = ctx.Response;

            // CORS
            if (request.Headers.Get("Origin") != null)
            {
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            }

            try
            {
                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (request.Url?.AbsolutePath == "/mcp")
                {
                    if (request.HttpMethod == "POST")
                    {
                        using var reader = new StreamReader(request.InputStream);
                        var body = await reader.ReadToEndAsync();
                        McpLog.Info($"[http] POST /mcp: {body.Substring(0, Math.Min(body.Length, 200))}");

                        var message = JsonSerializer.Deserialize<JsonRpcMessage>(body, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
                        if (message != null)
                        {
                            var hasResult = await transport.HandlePostRequestAsync(message, response.OutputStream, ct);
                            response.ContentType = "application/json";
                            response.Close();
                        }
                        else
                        {
                            response.StatusCode = 202;
                            response.Close();
                        }
                    }
                    else if (request.HttpMethod == "GET")
                    {
                        await transport.HandleGetRequestAsync(response.OutputStream, ct);
                    }
                    else if (request.HttpMethod == "DELETE")
                    {
                        response.StatusCode = 204;
                        response.Close();
                    }
                    else
                    {
                        response.StatusCode = 405;
                        response.Close();
                    }
                }
                else if (request.HttpMethod == "GET")
                {
                    var json = "{\"status\":\"ok\",\"server\":\"RimWorldMCP\",\"transport\":\"http\",\"endpoints\":[\"/mcp\"]}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    response.ContentType = "application/json";
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    response.Close();
                }
                else
                {
                    response.StatusCode = 404;
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                McpLog.Info($"[http] 处理请求错误: {ex.Message}");
                try { response.StatusCode = 500; response.Close(); } catch { }
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
                var asmPath = typeof(GameComponent_McpServer).Assembly.Location;
                if (string.IsNullOrEmpty(asmPath))
                    return FallbackSkillsDir();

                var asmDir = Path.GetDirectoryName(asmPath);
                if (asmDir == null)
                    return FallbackSkillsDir();

                var modRoot = Path.GetFullPath(Path.Combine(asmDir, "..", ".."));
                var skillsDir = Path.Combine(modRoot, "Skills");

                McpLog.Info($"尝试 Skills 路径: {skillsDir}");
                if (Directory.Exists(skillsDir))
                    return skillsDir;

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
