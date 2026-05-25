using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.Harmony;
using RimWorldMCP.Mcp;
using Verse;

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

                    // 工具结束时补推剩余通知 + 检查暂停状态
                    try
                    {
                        var afterResult = await McpCommandQueue.DispatchAsync(() =>
                        {
                            var remainingStr = NotificationBus.DrainFormatted();
                            var pauseStatus = GameContextProvider.BuildPauseStatus();
                            var paused = Find.TickManager?.Paused ?? false;
                            return new { Remaining = remainingStr, IsPaused = paused, PauseStatus = pauseStatus };
                        });
                        if (!string.IsNullOrEmpty(afterResult.Remaining))
                        {
                            // 通知已发送到 CC Companion，无需额外入队
                        }
                        if (afterResult.IsPaused)
                        {
                            result.Text += $"\n\n{afterResult.PauseStatus}";
                        }
                    }
                    catch { /* 调度失败不影响工具结果 */ }

                    return new ToolCallResult
                    {
                        Content = new List<ContentItem>
                        {
                            new() { Type = "text", Text = result.Text }
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
