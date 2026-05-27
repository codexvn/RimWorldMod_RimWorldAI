using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_DraftPawn : ITool
    {
        public string Name => "draft_pawn";
        public string Description => "征召或解除征召殖民者。征召后殖民者进入战斗状态，中断当前工作。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists，留空则操作全部）" },
                drafted = new { type = "boolean", description = "true=征召, false=解除征召" }
            },
            required = new[] { "drafted" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            // 参数验证（任意线程安全）
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("drafted", out var d)
                || d.ValueKind != JsonValueKind.True && d.ValueKind != JsonValueKind.False)
                return ToolResult.Error("缺少 drafted（需要 true 或 false）");

            var drafted = d.GetBoolean();
            int thingId = -1;
            if (args.Value.TryGetProperty("thing_id", out var jId) && jId.TryGetInt32(out var tid))
                thingId = tid;

            // 所有游戏 API 访问通过 DispatchAsync 调度到主线程
            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("当前没有可用地图。");

                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有殖民者。");

                    // 查找目标殖民者
                    List<Pawn> targets;
                    if (thingId < 0)
                    {
                        targets = colonists.ToList();
                    }
                    else
                    {
                        var p = colonists.FirstOrDefault(c => c.thingIDNumber == thingId);
                        if (p == null)
                            return ToolResult.Error($"找不到 ID={thingId} 的殖民者。使用 get_colonists 查看可用殖民者。");
                        targets = new List<Pawn> { p };
                    }

                    // 检查 drafter 可用性
                    var invalidPawns = targets.Where(p => p.drafter == null).ToList();
                    if (invalidPawns.Count > 0)
                    {
                        var names = invalidPawns.Select(p => p.Name.ToStringShort);
                        return ToolResult.Error($"以下殖民者无法征召（非玩家控制）: {string.Join(", ", names)}");
                    }

                    // 执行征召/解除征召（跳过倒地或濒死休眠的殖民者）
                    var skippedPawns = new List<string>();
                    int draftedCount = 0;
                    foreach (var pawn in targets)
                    {
                        if (pawn.Downed)
                        {
                            skippedPawns.Add($"{pawn.Name.ToStringShort} (已倒地)");
                            continue;
                        }
                        if (pawn.Deathresting)
                        {
                            skippedPawns.Add($"{pawn.Name.ToStringShort} (濒死休眠中)");
                            continue;
                        }
                        pawn.drafter.Drafted = drafted;
                        draftedCount++;
                    }

                    if (draftedCount == 0)
                    {
                        if (skippedPawns.Count > 0)
                            return ToolResult.Error($"无法执行征召：{string.Join("、", skippedPawns)}");
                        return ToolResult.Error("没有可以操作的殖民者。");
                    }

                    // 构建结果消息
                    var actionText = drafted ? "已征召" : "已解除征召";
                    var sb = new StringBuilder();

                    if (draftedCount == 1 && targets.Count <= 1)
                    {
                        sb.Append($"{targets[0].Name.ToStringShort} (ID:{targets[0].thingIDNumber}) {actionText}");
                    }
                    else if (thingId < 0 && skippedPawns.Count == 0)
                    {
                        sb.Append($"全体殖民者({draftedCount}人) {actionText}");
                    }
                    else
                    {
                        sb.Append($"{draftedCount} 名殖民者 {actionText}");
                    }

                    if (drafted)
                        sb.AppendLine("。殖民者将中断当前工作并进入战斗状态。");
                    else
                        sb.AppendLine("。殖民者将恢复日常工作。");

                    if (skippedPawns.Count > 0)
                        sb.AppendLine($"注意：{skippedPawns.Count} 名殖民者被跳过（{string.Join("、", skippedPawns)}）。");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"征召操作失败: {ex.Message}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("thing_id", out var jT) || !jT.TryGetInt32(out var id)) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;
            var pawn = CameraHelper.FindPawnById(map, id);
            if (pawn == null) return null;
            return (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
        }
    }
}
