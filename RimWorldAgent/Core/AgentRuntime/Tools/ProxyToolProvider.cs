using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 游戏 MCP 工具代理。网关模式：暴露单一 game_cmd 工具，大幅减少上下文。
    /// game_cmd 通过 action 指定游戏命令名、params 传递参数，内部路由到 McpClient。
    /// </summary>
    public class ProxyToolProvider : SimpleMspServer.Mcp.IToolProvider
    {
        private const string GATEWAY_NAME = "game_cmd";

        private readonly McpClient _mcp;
        private List<MspToolDef> _cachedDefinitions = new();
        private JsonElement _cachedGatewaySchema;

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
                _cachedGatewaySchema = BuildGatewaySchema();
                CoreLog.Info($"[ProxyToolProvider] 网关模式加载 {_cachedDefinitions.Count} 个游戏工具 " +
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
                new MspToolDef
                {
                    Name = GATEWAY_NAME,
                    Description = "统一游戏命令入口。通过 action 指定命令名、params 传递参数。参数错误时会返回该命令的完整文档。",
                    InputSchema = _cachedGatewaySchema
                }
            };
        }

        async Task<MspToolCallResult> SimpleMspServer.Mcp.IToolProvider.ExecuteAsync(string name, JsonElement? args)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 解析网关参数：提取 action 和 params
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
                return new MspToolCallResult
                {
                    IsError = true,
                    Content = new List<MspContentItem>
                    {
                        new MspContentItem { Type = "text", Text = $"参数解析失败: {ex.Message}\n\n请使用 {{\"action\": \"工具名\", \"params\": {{...}}}} 格式。" }
                    }
                };
            }

            if (string.IsNullOrEmpty(innerName))
            {
                sw.Stop();
                return new MspToolCallResult
                {
                    IsError = true,
                    Content = new List<MspContentItem>
                    {
                        new MspContentItem { Type = "text", Text = "缺少 action 参数。请指定要执行的游戏命令名。" }
                    }
                };
            }

            // 调用游戏 MCP
            try
            {
                var result = await _mcp.CallTool(innerName, innerArgs);
                var suffix = await ToolDispatcher.BuildModeSuffixAsync();

                sw.Stop();
                CoreLog.Info($"[ProxyToolProvider] game_cmd → {innerName} 完成 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms");

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
                sw.Stop();
                var innerMsg = ex.InnerException != null
                    ? $" | Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}" : "";
                CoreLog.Error($"[ProxyToolProvider] game_cmd → {innerName} 失败 耗时 {sw.Elapsed.TotalMilliseconds:F0}ms: {ex.Message}{innerMsg}");

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

        /// <summary>从缓存构建 game_cmd 的 InputSchema（action enum + description）</summary>
        private JsonElement BuildGatewaySchema()
        {
            var actionEnum = _cachedDefinitions.Select(d => d.Name).OrderBy(n => n).ToArray();
            var actionDesc = BuildActionDescription();

            return JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        @enum = actionEnum,
                        description = actionDesc
                    },
                    @params = new
                    {
                        type = "object",
                        description = "命令参数 JSON。可选，无参数命令可省略。参数错误时会返回该命令的完整参数 Schema。",
                        additionalProperties = true
                    }
                },
                required = new[] { "action" }
            });
        }

        /// <summary>构建 action description：逐行列出全部工具名+简述</summary>

        private string BuildActionDescription()
        {
            var lines = new List<string> { $"游戏命令名（共 {_cachedDefinitions.Count} 个）：" };
            foreach (var def in _cachedDefinitions.OrderBy(d => d.Name))
            {
                var desc = def.Description ?? "";
                // 截取第一句或前 40 字符作为简述
                var requiresAdvance = def.Annotations?.RequiresAdvanceHint == true;
                var tag = requiresAdvance ? "【需advance_tick】" : "";
                var brief = desc.Length > 40 ? desc.Substring(0, 40) + "…" : desc;
                lines.Add($"- {def.Name}: {brief}");
            }
            return string.Join("\n", lines);
        }

        /// <summary>从缓存查找指定工具的完整参数文档</summary>
        private string? FindToolHelp(string actionName)
        {
            var def = _cachedDefinitions.FirstOrDefault(d => d.Name == actionName);
            if (def == null) return null;

            var schema = def.InputSchema;
            var desc = def.Description ?? "";

            return $"工具名: {actionName}\n描述: {desc}\n参数 Schema:\n{schema}";
        }

        List<MspResourceDef> SimpleMspServer.Mcp.IToolProvider.GetResources() => new();
        string? SimpleMspServer.Mcp.IToolProvider.ReadResource(string uri) => null;
    }
}