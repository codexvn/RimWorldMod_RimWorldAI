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
                    catch { }
                }
            }
            catch { }
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
            if (_tools.TryGetValue(name, out var tool))
            {
                try
                {
                    // 自动移动视角 — 工具自身返回目标区域，开关打开则移动+自动缩放
                    if (RimWorldMCPMod.Instance?.Settings?.AutoMoveCamera == true)
                    {
                        var range = tool.GetTargetRange(args);
                        if (range != null)
                            await CameraHelper.MoveToRange(range.Value.minX, range.Value.minZ, range.Value.maxX, range.Value.maxZ);
                    }

                    var result = await tool.ExecuteAsync(args);

                    // 收集前置通知消息（显示在工具结果之前）
                    var prefixes = new List<string>();

                    // L3 事件暂停中 → 注入摘要催促 AI 先学技能再收尾
                    if (BridgeLifecycle.DangerPaused)
                        prefixes.Add($"⚠ {BridgeLifecycle.DangerSummary} | 已暂停。建议先用 get_skills 查看可用领域技能，用 active_skill 获取知识后再处理。");

                    // L1+L2 非高危通知 → 注入计数，AI 自行决定是否暂停
                    int pendingCount = BridgeLifecycle.PendingLevel12Count;
                    if (pendingCount > 0 && !BridgeLifecycle.DangerPaused)
                    {
                        prefixes.Add($"📋 新事件: {pendingCount}件 | 暂停后用 get_skills 查看可用技能，active_skill 获取知识后处理。");
                        BridgeLifecycle.ResetPendingLevel12Count();
                    }

                    // 低速警告 → 注入一次
                    var slowWarn = Tool_AdvanceTick.GetLowSpeedWarning();
                    if (slowWarn != null)
                        prefixes.Add($"⏱ {slowWarn}");

                    if (prefixes.Count > 0)
                    {
                        var prefixText = string.Join("\n\n", prefixes) + "\n\n---\n\n";
                        result = ToolResult.Success(prefixText + (result.Text ?? ""));
                    }

                    // 工具结束时补推剩余通知
                    try
                    {
                        await McpCommandQueue.DispatchAsync(() =>
                        {
                            NotificationBus.DrainFormatted();
                            return true;
                        });
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
