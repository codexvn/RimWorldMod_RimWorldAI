using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Harmony;
using RimWorldMCP.Mcp;

namespace RimWorldMCP.Tools
{
    public class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools = new();
        private readonly List<ResourceDefinition> _resources = new();

        public void Register(ITool tool)
        {
            _tools[tool.Name] = tool;
        }

        public void RegisterResource(string uri, string name, string description)
        {
            _resources.Add(new ResourceDefinition
            {
                Uri = uri,
                Name = name,
                Description = description,
                MimeType = "text/plain"
            });
        }

        public List<ToolDefinition> GetDefinitions()
        {
            return _tools.Values.Select(t =>
            {
                var def = new ToolDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = t.InputSchema,
                    Annotations = GetAnnotations(t.Name)
                };
                return def;
            }).ToList();
        }

        private static ToolAnnotations? GetAnnotations(string toolName)
        {
            if (toolName.StartsWith("get_") || toolName.StartsWith("list_"))
            {
                return new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false };
            }
            if (toolName == "schedule_operation")
            {
                return new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true };
            }
            return new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true };
        }

        public List<ResourceDefinition> GetResources()
        {
            return _resources;
        }

        private static async Task<string> BuildGameMessageSuffixAsync()
        {
            var buffered = new List<string>();
            // 从 Tick() 推送的格式化队列中取
            while (GatewayEventMonitor.RecentNotifications.TryDequeue(out var msg))
                buffered.Add(msg);

            // 取走 Tick 周期间 Harmony Patch 拦截但尚未格式化的通知
            string? unprocessed = null;
            try
            {
                unprocessed = await McpCommandQueue.DispatchAsync(
                    () => NotificationBus.DrainFormatted());
            }
            catch { /* 调度失败不影响工具结果 */ }

            if (buffered.Count == 0 && string.IsNullOrEmpty(unprocessed)) return "";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("### 游戏消息");
            foreach (var m in buffered)
                sb.AppendLine($"- {m}");
            if (!string.IsNullOrEmpty(unprocessed))
            {
                foreach (var line in unprocessed.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine(line);
                }
            }

            return sb.ToString();
        }

        public string? ReadResource(string uri)
        {
            return _resources.Any(r => r.Uri == uri)
                ? "资源内容（待游戏 API 接入后实现实时数据）"
                : null;
        }

        public async Task<ToolCallResult> ExecuteAsync(string name, JsonElement? args)
        {
            if (_tools.TryGetValue(name, out var tool))
            {
                try
                {
                    var result = await tool.ExecuteAsync(args);

                    // 捕获工具调用间隙的游戏内消息
                    var gameMessages = await BuildGameMessageSuffixAsync();

                    return new ToolCallResult
                    {
                        Content = new List<ContentItem>
                        {
                            new() { Type = "text", Text = result.Text + gameMessages }
                        },
                        IsError = result.IsError
                    };
                }
                catch (Exception ex)
                {
                    return new ToolCallResult
                    {
                        Content = new List<ContentItem>
                        {
                            new() { Type = "text", Text = $"Tool 执行异常: {ex.Message}" }
                        },
                        IsError = true
                    };
                }
            }

            return new ToolCallResult
            {
                Content = new List<ContentItem>
                {
                    new()
                    {
                        Type = "text",
                        Text = $"未知工具: {name}。可用工具: {string.Join(", ", _tools.Keys)}"
                    }
                },
                IsError = true
            };
        }
    }
}
