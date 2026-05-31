using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RimWorldMCP.Harmony;
using SimpleMspServer.Mcp;
using Verse;

namespace RimWorldMCP.Tools
{
    public class ToolRegistry : SimpleMspServer.Mcp.IToolProvider
    {
        string SimpleMspServer.Mcp.IToolProvider.ProviderName => "RimWorldMCP";
        private readonly Dictionary<string, ITool> _tools = new();
        public IReadOnlyDictionary<string, ITool> AllTools => _tools;

        /// <summary>并发控制：任何时候只允许一个工具在 MCP Server 侧执行</summary>
        private static readonly SemaphoreSlim _gate = new(1, 1);

        /// <summary>工具结果后缀：Agent 通过 set_tool_result_suffix 设置，每次工具调用结果末尾自动追加</summary>
        public static volatile string ToolResultSuffix = "";
        private readonly List<ResourceDefinition> _resources = new();

        /// <summary>自动扫描：支持 GetTargetRange 返回非 null 坐标的工具名称集合</summary>
        private static readonly HashSet<string> s_cameraToolNames = new();

        static ToolRegistry()
        {
            try
            {
                foreach (var type in typeof(ToolRegistry).Assembly.GetTypes())
                {
                    if (!typeof(ITool).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
                        continue;
                    try
                    {
                        var tool = (ITool)Activator.CreateInstance(type);
                        if (tool.Name == "move_camera") continue; // 跳过自身
                        using var doc = JsonDocument.Parse("{\"pos_x\":0,\"pos_y\":0}");
                        if (tool.GetTargetRange(doc.RootElement) != null)
                            s_cameraToolNames.Add(tool.Name);
                    }
                    catch (Exception ex) { Log.Warning($"[ToolRegistry] 测试工具 GetTargetRange 失败 ({type.Name}): {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Warning($"[ToolRegistry] 反射扫描 Tool 失败: {ex.Message}"); }
        }

        /// <summary>获取所有支持自动移动视角的工具名称（已排序）</summary>
        public static IReadOnlyList<string> CameraToolNames => s_cameraToolNames.OrderBy(n => n).ToList();

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
            }).OrderBy(t => t.Name).ToList();
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
            await _gate.WaitAsync();
            try
            {
            if (_tools.TryGetValue(name, out var tool))
            {
                try
                {
                    // 地图加载守卫（Find.CurrentMap 是简单字段读取，线程安全无需 DispatchAsync）
                    if (!(tool is INoMapRequired) && Find.CurrentMap == null)
                    {
                        return new ToolCallResult
                        {
                            Content = new List<ContentItem>
                            {
                                new() { Type = "text", Text = "当前没有已加载的地图，请先加载游戏存档或开始新游戏。" }
                            },
                            IsError = true
                        };
                    }

                    // 游戏状态预检：在所有 DispatchAsync 之前拦截，防止主线程不可用时卡死
                    if (Current.Game == null)
                    {
                        return new ToolCallResult
                        {
                            Content = new List<ContentItem>
                            {
                                new() { Type = "text", Text = "游戏未启动（当前在主菜单）。请先开始新游戏或加载存档。" }
                            },
                            IsError = true
                        };
                    }
                    if (LongEventHandler.ForcePause)
                    {
                        return new ToolCallResult
                        {
                            Content = new List<ContentItem>
                            {
                                new() { Type = "text", Text = "游戏正在加载中，主线程暂时不可用，请稍后重试。" }
                            },
                            IsError = true
                        };
                    }

                    // 自动移动视角 — 工具自身返回目标区域，开关打开则移动+自动缩放
                    if (RimWorldMCPMod.Instance?.Settings?.AutoMoveCamera == true)
                    {
                        var range = tool.GetTargetRange(args);
                        if (range != null)
                            await CameraHelper.MoveToRange(range.Value.minX, range.Value.minZ, range.Value.maxX, range.Value.maxZ);
                    }

                    var result = await tool.ExecuteAsync(args);

                    // 追加 suffix（一次性，但 set_tool_result_suffix 自身不消费）
                    if (name != "set_tool_result_suffix")
                    {
                        var suffix = ToolResultSuffix;
                        if (!string.IsNullOrEmpty(suffix))
                        {
                            ToolResultSuffix = "";
                            result.Text = result.Text + "\n\n" + suffix;
                        }
                    }

                    // 追加游戏速度
                    try
                    {
                        var label = Tool_GetGameSpeed.GetSpeedLabel();
                        if (!string.IsNullOrEmpty(label))
                            result.Text = result.Text + $"\n\n[游戏速度: {label}]";
                    }
                    catch (Exception ex) { Log.Warning($"[ToolRegistry] 获取游戏速度失败: {ex.Message}"); }

                    // 工具结束时补推剩余通知
                    try
                    {
                        await McpCommandQueue.DispatchAsync(() =>
                        {
                            NotificationBus.DrainFormatted();
                            return true;
                        });
                    }
                    catch (Exception ex) { Log.Warning($"[ToolRegistry] 自动追踪事件调度失败: {ex.Message}"); }

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
            finally { _gate.Release(); }
        }
    }
}
