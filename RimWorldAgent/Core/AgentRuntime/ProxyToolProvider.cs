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
    /// <summary>将游戏 MCP 工具代理到 Agent MCP Server，使 SDK 只连 agent 一个端点。</summary>
    public class ProxyToolProvider : SimpleMspServer.Mcp.IToolProvider
    {
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
                CoreLog.Info($"[ProxyToolProvider] 加载 {_cachedDefinitions.Count} 个游戏工具 " +
                    $"(排除 {internalNames.Count} 个内部工具)");
            }
            catch (Exception ex)
            {
                CoreLog.Error($"[ProxyToolProvider] 加载工具列表失败: {ex.Message}");
            }
        }

        List<MspToolDef> SimpleMspServer.Mcp.IToolProvider.GetDefinitions() => _cachedDefinitions;

        async Task<MspToolCallResult> SimpleMspServer.Mcp.IToolProvider.ExecuteAsync(string name, JsonElement? args)
        {
            try
            {
                var dict = args != null
                    ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.Value.GetRawText())
                    : null;
                var result = await _mcp.CallTool(name, dict);
                var suffix = await ToolDispatcher.BuildModeSuffixAsync();

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
                CoreLog.Error($"[ProxyToolProvider] 工具 {name} 代理失败: {ex.Message}");
                return new MspToolCallResult
                {
                    IsError = true,
                    Content = new List<MspContentItem>
                    {
                        new MspContentItem { Type = "text", Text = $"工具 {name} 代理执行失败: {ex.Message}" }
                    }
                };
            }
        }

        List<MspResourceDef> SimpleMspServer.Mcp.IToolProvider.GetResources() => new();
        string? SimpleMspServer.Mcp.IToolProvider.ReadResource(string uri) => null;
    }
}
