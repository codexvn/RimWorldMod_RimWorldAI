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
    /// <summary>
    /// Colony 全局摘要 — 替代多次调用 get_game_context + get_resources + check_colony + get_colonists + find_enemies。
    /// 固定结构，配合 Prompt Cache。
    /// </summary>
    public class Tool_GetWorldSummary : ITool
    {
        public string Name => "get_world_summary";
        public string Description => "获取殖民地全局状态摘要：食物/资源/威胁/殖民者/研究/心情。一次性替代多次查询调用。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                include_chunks = new { type = "boolean", description = "是否同时返回相关 Chunk 列表（默认 false）" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            bool includeChunks = false;
            if (args != null && args.Value.TryGetProperty("include_chunks", out var jChunks))
                includeChunks = jChunks.GetBoolean();

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    var sb = new StringBuilder();
                    int day = Find.TickManager.TicksGame / 60000;
                    int hour = (Find.TickManager.TicksGame / 2500) % 24;
                    var season = GenLocalDate.Season(map).ToString();

                    // ---- 基础信息 ----
                    sb.AppendLine($"## 殖民地状态: {season}, Day {day}, {hour:D2}:00");
                    sb.AppendLine();

                    // ---- 资源 ----
                    sb.AppendLine("### 资源");
                    sb.AppendLine("| 资源 | 库存 |");
                    sb.AppendLine("|------|------|");
                    var res = map.resourceCounter;
                    foreach (var kv in res.AllCountedAmounts.OrderByDescending(kv => kv.Value))
                    {
                        if (kv.Value <= 0) continue;
                        var def = kv.Key;
                        if (def == ThingDefOf.Silver || def.IsNutritionGivingIngestible || def.IsMedicine ||
                            def == ThingDefOf.ComponentIndustrial || def == ThingDefOf.ComponentSpacer ||
                            def == ThingDefOf.Steel || def == ThingDefOf.WoodLog || def == ThingDefOf.Plasteel ||
                            def == ThingDefOf.Cloth || def == ThingDefOf.Uranium)
                        {
                            sb.AppendLine($"| {def.LabelCap} | {kv.Value} |");
                        }
                    }
                    sb.AppendLine();

                    // ---- 殖民者 ----
                    sb.AppendLine("### 殖民者");
                    sb.AppendLine("| 名字 | 心情 | 健康 | 当前活动 |");
                    sb.AppendLine("|------|------|------|---------|");
                    int totalColonists = 0, idleCount = 0;
                    foreach (var pawn in PawnsFinder.AllMaps_FreeColonistsSpawned)
                    {
                        totalColonists++;
                        var mood = pawn.needs?.mood?.CurLevelPercentage ?? 1f;
                        var moodIcon = mood > 0.7f ? "😊" : mood > 0.4f ? "😐" : mood > 0.15f ? "😟" : "🔴";
                        var healthOk = !pawn.health.hediffSet.HasTendableHediff();
                        var healthText = healthOk ? "OK" : "⚠ 需治疗";
                        var curJob = pawn.CurJob?.GetReport(pawn) ?? "待命中";
                        if (pawn.mindState?.IsIdle == true) { curJob = "空闲"; idleCount++; }

                        sb.AppendLine($"| {pawn.LabelShort} (ID:{pawn.thingIDNumber}) | {moodIcon} {mood * 100:F0}% | {healthText} | {curJob} |");
                    }
                    sb.AppendLine();

                    // ---- 食物 ----
                    float foodDays = 0f;
                    foreach (var kv in res.AllCountedAmounts)
                    {
                        if (kv.Key.IsNutritionGivingIngestible && kv.Key.ingestible?.HumanEdible == true)
                            foodDays += kv.Value * kv.Key.ingestible.CachedNutrition / (totalColonists * 1.6f);
                    }
                    var foodStatus = foodDays < 3f ? "🔴 危机" : foodDays < 5f ? "⚠ 紧张" : "✅ 充足";
                    sb.AppendLine($"### 食物: {foodStatus} ({foodDays:F1} 天储备, {totalColonists}人)");
                    sb.AppendLine();

                    // ---- 威胁 ----
                    int enemyCount = 0, downedEnemyCount = 0;
                    var enemyGroups = new Dictionary<string, (int count, int nearX, int nearZ, float nearDist, string dir)>();
                    var colonyCenter = new IntVec3(map.Size.x / 2, 0, map.Size.z / 2);

                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn.Faction == null || !pawn.Faction.HostileTo(Faction.OfPlayer)) continue;
                        if (pawn.Downed) { downedEnemyCount++; continue; }
                        enemyCount++;

                        var key = pawn.KindLabel;
                        int dist = (int)pawn.Position.DistanceTo(colonyCenter);
                        string dir = pawn.Position.x < colonyCenter.x ? "西" : "东";
                        dir += pawn.Position.z < colonyCenter.z ? "北" : "南";

                        if (enemyGroups.TryGetValue(key, out var g))
                        {
                            if (dist < g.nearDist)
                                enemyGroups[key] = (g.count + 1, pawn.Position.x, pawn.Position.z, dist, dir);
                            else
                                enemyGroups[key] = (g.count + 1, g.nearX, g.nearZ, g.nearDist, g.dir);
                        }
                        else
                        {
                            enemyGroups[key] = (1, pawn.Position.x, pawn.Position.z, dist, dir);
                        }
                    }

                    if (enemyCount > 0)
                    {
                        sb.AppendLine($"### ⚠ 威胁: {enemyCount} 活跃敌人");
                        sb.AppendLine("| 名称 | 数量 | 最近位置 | 方向 |");
                        sb.AppendLine("|------|------|---------|------|");
                        foreach (var kv in enemyGroups)
                            sb.AppendLine($"| {kv.Key} | {kv.Value.count} | ({kv.Value.nearX},{kv.Value.nearZ}) | {kv.Value.dir}, ~{kv.Value.nearDist}格 |");
                        if (downedEnemyCount > 0) sb.AppendLine($"*另有 {downedEnemyCount} 名敌人已倒地*");
                        sb.AppendLine();
                    }

                    // ---- 无屋顶腐坏 ----
                    const float DeteriorationAlertValueThreshold = 500f; // 总价值超过500银时提示
                    float unroofedValue = 0f;
                    int unroofedItemCount = 0;
                    foreach (var t in map.listerThings.AllThings)
                    {
                        if (t.def.category != ThingCategory.Item) continue;
                        if (t.Position.Roofed(map)) continue;
                        if (StoreUtility.IsInValidBestStorage(t)) continue;
                        unroofedValue += t.MarketValue * t.stackCount;
                        unroofedItemCount++;
                    }
                    if (unroofedValue >= DeteriorationAlertValueThreshold)
                    {
                        sb.AppendLine($"### ⚠ 无屋顶腐坏风险: {unroofedItemCount} 件物品, 总价值 ~{unroofedValue:F0} 银");
                        sb.AppendLine("→ Economy: 建议建造屋顶或移至室内储藏区");
                        sb.AppendLine();
                    }

                    // ---- 研究 ----
                    var curProj = Find.ResearchManager.GetProject();
                    if (curProj != null)
                    {
                        float progress = Find.ResearchManager.GetProgress(curProj);
                        float cost = curProj.CostFactor(Faction.OfPlayer.def.techLevel) > 0
                            ? curProj.baseCost / curProj.CostFactor(Faction.OfPlayer.def.techLevel)
                            : curProj.baseCost;
                        float pct = cost > 0 ? progress / curProj.baseCost * 100f : 0;
                        sb.AppendLine($"### 研究: {curProj.LabelCap} ({pct:F0}%)");
                        sb.AppendLine();
                    }

                    // ---- Chunks ----
                    if (includeChunks)
                    {
                        var settings = RimWorldMCPMod.Instance?.Settings;
                        int cw = settings?.ChunkWidth ?? 32;
                        int ch = settings?.ChunkHeight ?? 32;
                        var dims = MapRendering.MapChunker.GetGridDimensions(map.Size.x, map.Size.z, cw, ch);
                        sb.AppendLine($"### 地图: {map.Size.x}×{map.Size.z}, Chunk 网格: {dims.cols}×{dims.rows}");
                        sb.AppendLine("使用 list_chunks 获取 Chunk 列表，然后用具体 grid 工具按需加载。");
                        sb.AppendLine();
                    }

                    // ---- 活跃提醒 ----
                    sb.AppendLine("### 活跃提醒");
                    int alertCount = 0;
                    foreach (var letter in Find.LetterStack.LettersListForReading)
                    {
                        if (letter.def == LetterDefOf.ThreatBig || letter.def == LetterDefOf.ThreatSmall)
                        {
                            sb.AppendLine($"- 🔴 [{letter.def.label}] {letter.Label}");
                            alertCount++;
                        }
                    }
                    if (alertCount == 0) sb.AppendLine("- 无紧急提醒");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"获取摘要失败: {ex.Message}");
                }
            });
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
