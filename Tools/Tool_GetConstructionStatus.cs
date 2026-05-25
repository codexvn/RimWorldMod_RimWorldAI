using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_GetConstructionStatus : ITool
    {
        public string Name => "get_construction_status";
        public string Description => "获取地图上所有未完成的建造项目（蓝图和框架），包含材料缺口、工时进度、阻塞物。用于建造进度跟踪与资源调度。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
                    var frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);

                    if ((blueprints == null || blueprints.Count == 0) &&
                        (frames == null || frames.Count == 0))
                        return ToolResult.Success("## 建造状态\n\n无进行中的建造项目。");

                    var sb = new StringBuilder();
                    sb.AppendLine("## 建造状态");
                    sb.AppendLine();

                    // 蓝图（待交付材料）
                    if (blueprints != null && blueprints.Count > 0)
                    {
                        sb.AppendLine($"### 蓝图 ({blueprints.Count}) — 待交付材料/清理障碍");
                        sb.AppendLine();
                        BuildBlueprintList(sb, blueprints, map);
                    }

                    // 框架（建造中）
                    if (frames != null && frames.Count > 0)
                    {
                        if (blueprints != null && blueprints.Count > 0)
                            sb.AppendLine();
                        sb.AppendLine($"### 框架 ({frames.Count}) — 建造中");
                        sb.AppendLine();
                        BuildFrameList(sb, frames, map);
                    }

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"获取建造状态失败: {ex.Message}");
                }
            });
        }

        private static void BuildBlueprintList(StringBuilder sb, List<Thing> blueprints, Map map)
        {
            for (int i = 0; i < blueprints.Count; i++)
            {
                var bp = blueprints[i];
                var entityDef = bp.def.entityDefToBuild;
                string label = entityDef?.label ?? "未知";
                string? stuff = bp.Stuff?.label;
                string itemLabel = stuff != null ? $"{stuff}{label}" : label;
                var pos = bp.Position;

                sb.Append($"[{i + 1}] ({pos.x},{pos.z}) {itemLabel}");

                // 安装蓝图无材料成本
                if (bp is Blueprint_Install)
                {
                    float work = GetWorkTotal(bp);
                    sb.AppendLine(work > 0 ? $"  安装工时: {work:F0}" : "");
                    continue;
                }

                // 材料缺口
                var resources = GetResourceGap(bp, map);
                if (resources.Count > 0)
                {
                    sb.Append("  缺: ");
                    var parts = new List<string>();
                    foreach (var res in resources)
                    {
                        string flag = res.Needed > res.OnMap ? "⚠" : "";
                        parts.Add($"{res.Label}x{res.Needed}(库存{res.OnMap}){flag}");
                    }
                    sb.Append(string.Join(", ", parts));
                }

                // 工时
                float workTotal = GetWorkTotal(bp);
                if (workTotal > 0)
                    sb.Append($"  工时: {workTotal:F0}");

                // 阻塞物
                var blocking = FirstBlocking(bp);
                if (blocking != null)
                    sb.Append($"  阻塞: {blocking}");

                sb.AppendLine();
            }
        }

        private static void BuildFrameList(StringBuilder sb, List<Thing> frames, Map map)
        {
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var entityDef = frame.def.entityDefToBuild;
                string label = entityDef?.label ?? "未知";
                string? stuff = frame.Stuff?.label;
                string itemLabel = stuff != null ? $"{stuff}{label}" : label;
                var pos = frame.Position;

                sb.Append($"[{i + 1}] ({pos.x},{pos.z}) {itemLabel}");

                // 材料
                var resources = GetResourceGap(frame, map);
                if (resources.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var res in resources)
                    {
                        if (res.Needed == 0)
                            parts.Add($"{res.Label} ✓");
                        else
                        {
                            string flag = res.Needed > res.OnMap ? "⚠" : "";
                            parts.Add($"{res.Label} 缺{res.Needed}(库存{res.OnMap}){flag}");
                        }
                    }
                    sb.Append($"  材料: {string.Join(", ", parts)}");
                }
                else
                {
                    sb.Append("  材料: ✓齐全");
                }

                // 工时进度
                float workTotal = GetWorkTotal(frame);
                float workDone = 0;
                if (frame is Frame f)
                {
                    workDone = f.workDone;
                    float workLeft = f.WorkLeft;
                    float pct = workTotal > 0 ? workDone / workTotal * 100f : 0;
                    sb.Append($"  工时: {workDone:F0}/{workTotal:F0}({pct:F0}%)");
                    if (workLeft <= 0 && resources.All(r => r.Needed == 0))
                        sb.Append("  ⏳待完工");
                }
                else
                {
                    sb.Append($"  工时: {workTotal:F0}");
                }

                // 阻塞物
                var blocking = FirstBlocking(frame);
                if (blocking != null)
                    sb.Append($"  阻塞: {blocking}");

                sb.AppendLine();
            }
        }

        private struct ResourceGap
        {
            public string Label;
            public int Needed;
            public int OnMap;
        }

        private static List<ResourceGap> GetResourceGap(Thing thing, Map map)
        {
            var list = new List<ResourceGap>();
            if (!(thing is IConstructible constructible)) return list;

            var costs = constructible.TotalMaterialCost();
            if (costs == null) return list;

            foreach (var cost in costs)
            {
                int needed = constructible.ThingCountNeeded(cost.thingDef);
                if (cost.count > 0 || needed > 0)
                {
                    list.Add(new ResourceGap
                    {
                        Label = cost.thingDef.label,
                        Needed = needed,
                        OnMap = map.resourceCounter.GetCount(cost.thingDef)
                    });
                }
            }

            return list;
        }

        private static float GetWorkTotal(Thing thing)
        {
            var entityDef = thing.def.entityDefToBuild;
            if (entityDef == null) return 0;
            return entityDef.GetStatValueAbstract(StatDefOf.WorkToBuild, thing.Stuff);
        }

        private static string? FirstBlocking(Thing thing)
        {
            try
            {
                var blocker = GenConstruct.FirstBlockingThing(thing, null);
                if (blocker == null) return null;
                return $"{blocker.Label}({blocker.Position.x},{blocker.Position.z})";
            }
            catch
            {
                return null;
            }
        }
    }
}
