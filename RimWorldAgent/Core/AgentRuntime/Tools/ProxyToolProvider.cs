using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldAgent.Core.Mcp;
using MspToolDef = SimpleMspServer.Mcp.ToolDefinition;
using MspContentItem = SimpleMspServer.Mcp.ContentItem;
using MspToolCallResult = SimpleMspServer.Mcp.ToolCallResult;
using MspResourceDef = SimpleMspServer.Mcp.ResourceDefinition;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>
    /// 游戏 MCP 工具代理。meta-tools 模式：暴露 discover_tools / get_tool_schema / execute_tool 三个元工具，
    /// 替代单体 game_cmd，按需暴露工具列表和参数 Schema，大幅减少上下文。
    /// </summary>
    public class ProxyToolProvider : SimpleMspServer.Mcp.IToolProvider
    {
        private const string TOOL_DISCOVER = "discover_tools";
        private const string TOOL_SCHEMA = "get_tool_schema";
        private const string TOOL_EXECUTE = "execute_tool";

        private readonly McpClient _mcp;
        private List<MspToolDef> _cachedDefinitions = new();

        public string ProviderName => "GameProxy";

        public ProxyToolProvider(McpClient mcp)
        {
            _mcp = mcp;
        }

        public async Task RefreshToolsAsync()
        {
            try
            {
                var tools = await _mcp.ListToolsAsync();
                var internalNames = new HashSet<string>(
                    InternalToolRegistry.Instance.All.Select(t => t.Name));
                _cachedDefinitions = tools
                    .Where(t => !internalNames.Contains(t.Name))
                    .Select(t => new MspToolDef
                    {
                        Name = t.Name,
                        Description = t.Description ?? "",
                        InputSchema = t.InputSchema
                    })
                    .ToList();
                CoreLog.Info($"[ProxyToolProvider] meta-tools 模式加载 {_cachedDefinitions.Count} 个游戏工具 " +
                    $"(排除 {internalNames.Count} 个内部工具)");
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[ProxyToolProvider] 加载工具列表失败: {ex.Message}");
            }
        }

        List<MspToolDef> SimpleMspServer.Mcp.IToolProvider.GetDefinitions()
        {
            return new List<MspToolDef>
            {
                BuildDiscoverSchema(),
                BuildGetSchemaSchema(),
                BuildExecuteSchema()
            };
        }

        async Task<MspToolCallResult> SimpleMspServer.Mcp.IToolProvider.ExecuteAsync(string name, JsonElement? args)
        {
            switch (name)
            {
                case TOOL_DISCOVER:
                    return DiscoverTools();
                case TOOL_SCHEMA:
                    return GetToolSchema(args);
                case TOOL_EXECUTE:
                    return await ExecuteTool(args);
                default:
                    return ErrorResult($"未知的 meta-tool: {name}");
            }
        }

        // ===== Tool 1: discover_tools =====

        private MspToolDef BuildDiscoverSchema()
        {
            return new MspToolDef
            {
                Name = TOOL_DISCOVER,
                Description = "列出全部可用游戏命令及其简述。",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new { }
                })
            };
        }

        private MspToolCallResult DiscoverTools()
        {
            if (_cachedDefinitions.Count == 0)
                return TextResult("游戏工具列表尚未加载，请稍后重试。");

            var sb = new StringBuilder();
            sb.AppendLine($"共 {_cachedDefinitions.Count} 个游戏命令：");
            sb.AppendLine();

            foreach (var def in _cachedDefinitions.OrderBy(d => d.Name))
            {
                var requiresAdvance = def.Annotations?.RequiresAdvanceHint == true;
                var tag = requiresAdvance ? " 【需advance_tick】" : "";
                sb.AppendLine($"- {def.Name}: {def.Description}{tag}");
            }

            return TextResult(sb.ToString().TrimEnd());
        }

        // ===== Tool 2: get_tool_schema =====

        private MspToolDef BuildGetSchemaSchema()
        {
            return new MspToolDef
            {
                Name = TOOL_SCHEMA,
                Description = "获取指定游戏命令的完整参数 Schema。支持数组批量查询。",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        actions = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "游戏命令名数组（从 discover_tools 获取）。传单个命令也用数组包裹，如 [\"spawn_pawn\"]。"
                        }
                    },
                    required = new[] { "actions" }
                })
            };
        }

        private MspToolCallResult GetToolSchema(JsonElement? args)
        {
            if (args == null)
                return ErrorResult("缺少 actions 参数。请传入命令名数组，如 {\"actions\": [\"get_colonists\"]}。");

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.Value.GetRawText());
                if (dict == null || !dict.TryGetValue("actions", out var actionsElem) ||
                    actionsElem.ValueKind != JsonValueKind.Array)
                    return ErrorResult("缺少 actions 参数，或格式不正确。请传入 {\"actions\": [\"命令名1\", \"命令名2\"]}。");

                var actionNames = new List<string>();
                foreach (var item in actionsElem.EnumerateArray())
                {
                    var name = item.GetString();
                    if (!string.IsNullOrEmpty(name))
                        actionNames.Add(name);
                }

                if (actionNames.Count == 0)
                    return ErrorResult("actions 数组为空，请指定至少一个命令名。");

                var sb = new StringBuilder();
                sb.AppendLine($"get_tool_schema 返回 {actionNames.Count} 个工具的 Schema：");

                foreach (var actionName in actionNames)
                {
                    sb.AppendLine();
                    sb.AppendLine($"━━━ {actionName} ━━━");

                    var def = _cachedDefinitions.FirstOrDefault(d => d.Name == actionName);
                    if (def == null)
                    {
                        sb.AppendLine($"未知命令: {actionName}");
                        continue;
                    }

                    sb.AppendLine($"描述: {def.Description}");
                    sb.AppendLine();

                    // 人类可读参数列表
                    sb.Append(FormatSchemaHumanReadable(def.InputSchema));

                    sb.AppendLine();
                    sb.AppendLine($"JSON Schema: {def.InputSchema}");
                }

                return TextResult(sb.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return ErrorResult($"解析参数失败: {ex.Message}");
            }
        }

        /// <summary>从 JSON Schema 提取人类可读的参数列表</summary>
        private static string FormatSchemaHumanReadable(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object)
                return "参数: (无 Schema)\n";

            if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
                return "参数: (无)\n";

            var required = new HashSet<string>();
            if (schema.TryGetProperty("required", out var reqArr) && reqArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in reqArr.EnumerateArray())
                {
                    var rn = r.GetString();
                    if (rn != null) required.Add(rn);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine("参数:");
            foreach (var prop in props.EnumerateObject())
            {
                var name = prop.Name;
                var p = prop.Value;
                var type = p.TryGetProperty("type", out var t) ? t.GetString() ?? "any" : "any";
                var desc = p.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var isRequired = required.Contains(name) ? "必填" : "可选";
                sb.AppendLine($"  {name} ({type}, {isRequired}): {desc}");
            }

            return sb.ToString();
        }

        // ===== Tool 3: execute_tool =====

        private MspToolDef BuildExecuteSchema()
        {
            return new MspToolDef
            {
                Name = TOOL_EXECUTE,
                Description = "执行指定的游戏命令。建议先通过 get_tool_schema 了解参数结构。参数错误时会返回完整 Schema 辅助修正。",
                InputSchema = JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        action = new
                        {
                            type = "string",
                            description = "游戏命令名（从 discover_tools 获取）"
                        },
                        @params = new
                        {
                            type = "object",
                            description = "命令参数。参数错误时会返回完整 Schema 辅助修正。",
                            additionalProperties = true
                        }
                    },
                    required = new[] { "action" }
                })
            };
        }

        private async Task<MspToolCallResult> ExecuteTool(JsonElement? args)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 解析参数：提取 action 和 params
            string? innerName = null;
            Dictionary<string, JsonElement>? innerArgs = null;

            try
            {
                if (args != null)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.Value.GetRawText());
                    if (dict != null)
                    {
                        if (dict.TryGetValue("action", out var actionElem))
                            innerName = actionElem.GetString();

                        if (dict.TryGetValue("params", out var paramsElem) &&
                            paramsElem.ValueKind == JsonValueKind.Object)
                            innerArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(paramsElem.GetRawText());
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                CoreLog.Error($"[ProxyToolProvider] 参数解析失败 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms: {ex.Message}");
                return ErrorResult($"参数解析失败: {ex.Message}\n\n请使用 {{\"action\": \"工具名\", \"params\": {{...}}}} 格式。");
            }

            if (string.IsNullOrEmpty(innerName))
            {
                sw.Stop();
                return ErrorResult("缺少 action 参数。请指定要执行的游戏命令名。");
            }

            // advance_tick 标记 — 推进期间 EnforcePauseAsync 不干预
            // advance_tick 推进期间 EnforcePauseAsync 不干预；失败时立即清除
            bool isAdvance = innerName == "advance_tick";
            if (isAdvance) AgentOrchestrator.IsAdvancing = true;
            try
            {
                var result = await _mcp.CallTool(innerName, innerArgs);
                var suffix = await ToolDispatcher.BuildModeSuffixAsync();

                sw.Stop();
                CoreLog.Info($"[ProxyToolProvider] execute_tool → {innerName} 完成 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms");

                return new MspToolCallResult
                {
                    Content = new List<MspContentItem>
                    {
                        new MspContentItem { Type = "text", Text = result + suffix }
                    }
                };
            }
            catch (Exception ex)
            {
                if (isAdvance) AgentOrchestrator.IsAdvancing = false;
                sw.Stop();
                var innerMsg = ex.InnerException != null
                    ? $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                CoreLog.Error($"[ProxyToolProvider] execute_tool → {innerName} 失败 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms: {ex.Message}{innerMsg}");

                // 参数错误时附带该工具的完整参数文档
                var help = FindToolHelp(innerName);
                var helpSuffix = help != null
                    ? $"\n\n━━━ {innerName} 参数文档 ━━━\n{help}"
                    : "";

                return new MspToolCallResult
                {
                    IsError = true,
                    Content = new List<MspContentItem>
                    {
                        new MspContentItem { Type = "text", Text = $"工具 {innerName} 执行失败: {ex.Message}{helpSuffix}" }
                    }
                };
            }
        }

        // ===== 辅助方法 =====

        /// <summary>从缓存查找指定工具的完整参数文档</summary>
        private string? FindToolHelp(string actionName)
        {
            var def = _cachedDefinitions.FirstOrDefault(d => d.Name == actionName);
            if (def == null) return null;

            var schema = def.InputSchema;
            var desc = def.Description ?? "";

            return $"工具名: {actionName}\n描述: {desc}\n参数 Schema:\n{schema}";
        }

        private static MspToolCallResult TextResult(string text)
        {
            return new MspToolCallResult
            {
                Content = new List<MspContentItem>
                {
                    new MspContentItem { Type = "text", Text = text }
                }
            };
        }

        private static MspToolCallResult ErrorResult(string text)
        {
            return new MspToolCallResult
            {
                IsError = true,
                Content = new List<MspContentItem>
                {
                    new MspContentItem { Type = "text", Text = text }
                }
            };
        }

        List<MspResourceDef> SimpleMspServer.Mcp.IToolProvider.GetResources() => new();
        string? SimpleMspServer.Mcp.IToolProvider.ReadResource(string uri) => null;
    }
}
