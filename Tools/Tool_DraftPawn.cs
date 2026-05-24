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
                colonist_name = new { type = "string", description = "殖民者名称。留空则操作全部。" },
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
            var nameFilter = "";
            if (args.Value.TryGetProperty("colonist_name", out var cn))
                nameFilter = cn.GetString() ?? "";

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
                    if (string.IsNullOrEmpty(nameFilter))
                    {
                        targets = colonists.ToList();
                    }
                    else
                    {
                        targets = colonists.Where(c =>
                            c.Name.ToStringShort.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0
                            || c.Name.ToStringFull.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0
                        ).ToList();

                        if (targets.Count == 0)
                            return ToolResult.Error($"未找到匹配的殖民者: {nameFilter}。使用 get_colonists 查看可用殖民者。");
                    }

                    // 检查 drafter 可用性
                    var invalidPawns = targets.Where(p => p.drafter == null).ToList();
                    if (invalidPawns.Count > 0)
                    {
                        var names = invalidPawns.Select(p => p.Name.ToStringShort);
                        return ToolResult.Error($"以下殖民者无法征召（非玩家控制）: {string.Join(", ", names)}");
                    }

                    // 执行征召/解除征召
                    foreach (var pawn in targets)
                    {
                        pawn.drafter.Drafted = drafted;
                    }

                    // 构建结果消息
                    var actionText = drafted ? "已征召" : "已解除征召";
                    var sb = new StringBuilder();

                    if (targets.Count == 1)
                    {
                        sb.Append($"{targets[0].Name.ToStringShort} {actionText}");
                    }
                    else if (string.IsNullOrEmpty(nameFilter))
                    {
                        sb.Append($"全体殖民者({targets.Count}人) {actionText}");
                    }
                    else
                    {
                        sb.Append($"{targets.Count} 名殖民者 {actionText}");
                    }

                    if (drafted)
                        sb.AppendLine("。殖民者将中断当前工作并进入战斗状态。");
                    else
                        sb.AppendLine("。殖民者将恢复日常工作。");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"征召操作失败: {ex.Message}");
                }
            });
        }
    }
}
