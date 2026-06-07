using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_ListDevices : ITool
    {
        public string Name => "list_devices";
        public string Description => "列出/搜索地图上的设备，支持按关键字、defName、Comp、action_id 和 UI 命令过滤，返回设备 ID 与可用操作摘要。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                keyword = new { type = "string", description = "模糊匹配关键字（匹配 Label 或 defName）" },
                defName = new { type = "string", description = "精确 defName 过滤" },
                thingDef = new { type = "string", description = "精确 defName 过滤（defName 的别名）" },
                label = new { type = "string", description = "按设备标签子串过滤" },
                comp = new { type = "string", description = "按 Comp 类型过滤，逗号分隔，如 CompTempControl,CompRefuelable" },
                action_id = new { type = "string", description = "仅返回支持该 adapter action_id 的设备，如 set_target_temp" },
                has_ui = new { type = "boolean", description = "仅返回有可见 UI/Gizmo 命令的设备", @default = false },
                page = new { type = "integer", description = "页码（1起始），默认1", @default = 1 },
                page_size = new { type = "integer", description = "每页条数，默认20，最大50", @default = 20 }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string keyword = "";
            if (args != null && args.Value.TryGetProperty("keyword", out var kw))
                keyword = kw.GetString() ?? "";

            string defName = "";
            if (args != null && args.Value.TryGetProperty("defName", out var dn))
                defName = dn.GetString() ?? "";
            else if (args != null && args.Value.TryGetProperty("thingDef", out var td))
                defName = td.GetString() ?? "";

            string label = "";
            if (args != null && args.Value.TryGetProperty("label", out var jl))
                label = jl.GetString() ?? "";

            string comp = "";
            if (args != null && args.Value.TryGetProperty("comp", out var jc))
                comp = jc.GetString() ?? "";

            string actionId = "";
            if (args != null && args.Value.TryGetProperty("action_id", out var ja))
                actionId = ja.GetString() ?? "";

            bool hasUi = false;
            if (args != null && args.Value.TryGetProperty("has_ui", out var jUi))
                hasUi = jUi.ValueKind == JsonValueKind.True;

            int page = 1, pageSize = 20;
            if (args?.TryGetProperty("page", out var jp) == true) page = Math.Max(1, jp.GetInt32());
            if (args?.TryGetProperty("page_size", out var jps) == true) pageSize = Math.Max(1, Math.Min(50, jps.GetInt32()));

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图。");

                    var compFilters = comp.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();

                    var devices = new List<(Thing thing, bool hasUi)>();
                    foreach (var thing in DeviceToolHelper.EnumerateDevices(map))
                    {
                        if (!string.IsNullOrEmpty(defName) && !thing.def.defName.Equals(defName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrEmpty(label) && thing.LabelCap.IndexOf(label, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!string.IsNullOrEmpty(keyword))
                        {
                            var matchLabel = thing.LabelCap.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                            var matchDef = thing.def.defName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!matchLabel && !matchDef) continue;
                        }
                        if (compFilters.Count > 0 && !compFilters.All(c => DeviceToolHelper.HasCompNamed(thing, c))) continue;
                        if (!string.IsNullOrEmpty(actionId) && !DeviceToolHelper.HasAdapterAction(thing, actionId)) continue;

                        var commands = DeviceToolHelper.GetDeviceCommands(new List<Thing> { thing });
                        var ui = commands.Count > 0;
                        if (hasUi && !ui) continue;
                        devices.Add((thing, ui));
                    }

                    if (devices.Count == 0)
                        return ToolResult.Success("未找到匹配的设备。");

                    var total = devices.Count;
                    var paged = devices
                        .OrderBy(d => d.thing.def.defName)
                        .ThenBy(d => d.thing.thingIDNumber)
                        .Skip((page - 1) * pageSize)
                        .Take(pageSize)
                        .ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 设备列表 共 {total} 条");
                    sb.AppendLine("| ID | 名称 | defName | 位置 | 关键组件 | adapter操作 | UI | ");
                    sb.AppendLine("|---:|---|---|---|---|---|---|");
                    foreach (var (thing, ui) in paged)
                    {
                        var line = DeviceToolHelper.FormatDeviceSummary(thing).TrimEnd('|');
                        sb.AppendLine($"{line}| {DeviceToolHelper.YesNo(ui)} |");
                    }

                    if (total > pageSize)
                    {
                        int totalPages = (int)Math.Ceiling((double)total / pageSize);
                        sb.AppendLine();
                        sb.AppendLine("---");
                        sb.Append($"第 {page}/{totalPages} 页，共 {total} 条");
                        if (page < totalPages) sb.Append($" | page={page + 1} 下一页");
                        if (page > 1) sb.Append($" | page={page - 1} 上一页");
                        sb.AppendLine();
                    }

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[ListDevices] 列出设备失败: {ex.GetType().Name}: {ex.Message}");
                    return ToolResult.Error($"列出设备失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
