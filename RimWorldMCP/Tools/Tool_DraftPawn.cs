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
    public class Tool_DraftPawn : ITool, IRequiresAdvanceTick
    {
        public string Name => "draft_pawn";
        public string Description => "征召或解除征召殖民者。征召后殖民者进入战斗状态并中断当前工作。fire_at_will 控制自动开火（默认 true）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                thing_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists，留空则操作全部）" },
                colonist_ids = new
                {
                    type = "array",
                    description = "指定殖民者 ID 列表（精确子集，优先级高于 thing_id）",
                    items = new { type = "integer" }
                },
                drafted = new { type = "boolean", description = "true=征召, false=解除征召" },
                fire_at_will = new { type = "boolean", description = "征召时是否自动开火（默认 true，对应游戏 UI 的 FireAtWill 开关）", @default = true }
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
            var fireAtWill = true;
            if (args.Value.TryGetProperty("fire_at_will", out var faw)
                && (faw.ValueKind == JsonValueKind.True || faw.ValueKind == JsonValueKind.False))
                fireAtWill = faw.GetBoolean();
            int thingId = -1;
            var colonistIds = new List<int>();
            if (args.Value.TryGetProperty("colonist_ids", out var jIds) && jIds.ValueKind == JsonValueKind.Array)
            {
                foreach (var ji in jIds.EnumerateArray())
                    if (ji.TryGetInt32(out var cid)) colonistIds.Add(cid);
            }
            if (colonistIds.Count == 0 && args.Value.TryGetProperty("thing_id", out var jId) && jId.TryGetInt32(out var tid))
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
                    if (colonistIds.Count > 0)
                    {
                        targets = colonists.Where(c => colonistIds.Contains(c.thingIDNumber)).ToList();
                        if (targets.Count == 0)
                            return ToolResult.Error($"找不到指定 ID 的殖民者: [{string.Join(",", colonistIds)}]");
                    }
                    else if (thingId < 0)
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
                        if (drafted) pawn.drafter.FireAtWill = fireAtWill;
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
                    {
                        sb.AppendLine();
                        sb.AppendLine();

                        // 每个被征召的殖民者附战斗简报 — 复用游戏 StatsReportUtility 统计管线
                        foreach (var pawn in targets)
                        {
                            if (pawn.Downed || pawn.Deathresting) continue;

                            var parts = new List<string>
                            {
                                $"**{pawn.Name.ToStringShort}**"
                            };

                            // 移动速度（游戏统计管线）
                            float moveSpeed = pawn.GetStatValue(StatDefOf.MoveSpeed, true, -1);
                            parts.Add($"移速:{moveSpeed:F1}格/秒");

                            // 护甲（游戏统计管线）
                            float sharp = pawn.GetStatValue(StatDefOf.ArmorRating_Sharp, true, -1);
                            float blunt = pawn.GetStatValue(StatDefOf.ArmorRating_Blunt, true, -1);
                            float heat = pawn.GetStatValue(StatDefOf.ArmorRating_Heat, true, -1);
                            if (sharp > 0.01f || blunt > 0.01f || heat > 0.01f)
                                parts.Add($"护甲:利刃{sharp:P0}/钝击{blunt:P0}/热能{heat:P0}");

                            parts.Add($"开火:{(pawn.drafter?.FireAtWill == true ? "是" : "否")}");

                            // 武器属性 — 复用游戏信息卡统计管线（遍历 StatDef → GetStatValue → 与 "i" 按钮一致）
                            var primary = pawn.equipment?.Primary;
                            if (primary != null)
                            {
                                var req = StatRequest.For(primary);
                                var weaponLabel = $"{primary.def?.label ?? "???"}";
                                var quality = primary.TryGetComp<CompQuality>();
                                if (quality != null) weaponLabel += $"({quality.Quality.GetLabel()})";
                                parts.Add(primary.def?.IsMeleeWeapon == true ? $"近战 [{weaponLabel}]" : $"远程 [{weaponLabel}]");

                                foreach (StatDef st in DefDatabase<StatDef>.AllDefs)
                                {
                                    try
                                    {
                                        if (!st.Worker.ShouldShowFor(req)) continue;
                                        if (st.Worker.IsDisabledFor(primary)) continue;
                                        // 只取武器相关分类
                                        var cat = st.category?.defName ?? "";
                                        if (!cat.Contains("Weapon") && !cat.Contains("Ranged") && !cat.Contains("Melee")) continue;

                                        float val = primary.GetStatValue(st, true, -1);
                                        parts.Add($"{st.LabelCap}:{st.ValueToString(val, ToStringNumberSense.Undefined)}");
                                    }
                                    catch (Exception ex)
                                    {
                                        McpLog.Warn($"[draft_pawn] 武器属性 {st.defName} 计算失败: {ex.Message}");
                                    }
                                }
                            }
                            else
                            {
                                parts.Add("空手");
                            }

                            sb.AppendLine(string.Join(" | ", parts));
                        }
                    }
                    else
                    {
                        sb.Append("。殖民者将恢复日常工作。");
                    }

                    if (skippedPawns.Count > 0)
                        sb.AppendLine($"注意：{skippedPawns.Count} 名殖民者被跳过（{string.Join("、", skippedPawns)}）。");

                    return ToolResult.Success(sb.ToString());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"征召操作失败: {FormatExceptionChain(ex)}");
                }
            });
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            var map = Find.CurrentMap;
            if (map == null) return null;

            int? minX = null, minZ = null, maxX = null, maxZ = null;
            if (args.Value.TryGetProperty("colonist_ids", out var jIds) && jIds.ValueKind == JsonValueKind.Array)
            {
                foreach (var ji in jIds.EnumerateArray())
                {
                    if (ji.TryGetInt32(out var id))
                        IncludePawn(CameraHelper.FindPawnById(map, id), ref minX, ref minZ, ref maxX, ref maxZ);
                }
                return minX.HasValue && minZ.HasValue && maxX.HasValue && maxZ.HasValue
                    ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value)
                    : ((int, int, int, int)?)null;
            }

            if (args.Value.TryGetProperty("thing_id", out var jT) && jT.TryGetInt32(out var thingId))
            {
                var pawn = CameraHelper.FindPawnById(map, thingId);
                if (pawn == null) return null;
                return (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
            }

            foreach (var pawn in PawnsFinder.AllMaps_FreeColonistsSpawned)
                IncludePawn(pawn, ref minX, ref minZ, ref maxX, ref maxZ);

            return minX.HasValue && minZ.HasValue && maxX.HasValue && maxZ.HasValue
                ? (minX.Value, minZ.Value, maxX.Value, maxZ.Value)
                : ((int, int, int, int)?)null;
        }

        private static void IncludePawn(Pawn? pawn, ref int? minX, ref int? minZ, ref int? maxX, ref int? maxZ)
        {
            if (pawn == null) return;
            minX = minX.HasValue ? Math.Min(minX.Value, pawn.Position.x) : pawn.Position.x;
            minZ = minZ.HasValue ? Math.Min(minZ.Value, pawn.Position.z) : pawn.Position.z;
            maxX = maxX.HasValue ? Math.Max(maxX.Value, pawn.Position.x) : pawn.Position.x;
            maxZ = maxZ.HasValue ? Math.Max(maxZ.Value, pawn.Position.z) : pawn.Position.z;
        }

        private static string FormatExceptionChain(Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}";
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                message += $" ← {inner.GetType().Name}: {inner.Message}";
            return message;
        }
    }
}
