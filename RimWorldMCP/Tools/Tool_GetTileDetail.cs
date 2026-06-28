using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RimWorldMCP.MapRendering;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetTileDetail : ITool
    {
        public string Name => "get_tile_detail";
        public string Description => "获取指定坐标范围内所有物品、建筑、植物的详细列表，含精确坐标。用于 LLM 精确了解某区域有什么。坐标范围为闭区间（两端坐标均包含）。";
        public JsonElement InputSchema
        {
            get
            {
                var mode = RimWorldMCPMod.Instance?.Settings?.GridQueryMode ?? GridQueryMode.Chunk;
                if (mode == GridQueryMode.Chunk)
                {
                    return JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new
                        {
                            chunk_id = new { type = "string", description = "Chunk ID (如 \"3_5\")，先用 list_chunks 获取可用 chunk。" },
                            pos_x = new { type = "integer", description = "左下 X 坐标（可选，chunk 模式下忽略）" },
                            pos_y = new { type = "integer", description = "左下 Y 坐标（可选，chunk 模式下忽略）" },
                            end_x = new { type = "integer", description = "右上 X 坐标（可选，不提供则只查单格）" },
                            end_y = new { type = "integer", description = "右上 Y 坐标（可选，不提供则只查单格）" }
                        },
                        required = new[] { "chunk_id" }
                    });
                }
                return JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        chunk_id = new { type = "string", description = "Chunk ID (如 \"3_5\")（可选，坐标模式下忽略）" },
                        pos_x = new { type = "integer", description = "左下 X 坐标（坐标模式下必填）" },
                        pos_y = new { type = "integer", description = "左下 Y 坐标（坐标模式下必填）" },
                        end_x = new { type = "integer", description = "右上 X 坐标（可选，不提供则只查单格）" },
                        end_y = new { type = "integer", description = "右上 Y 坐标（可选，不提供则只查单格）" }
                    },
                    required = new[] { "pos_x", "pos_y" }
                });
            }
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            int posX, posY, endX, endY;

            // chunk_id 优先
            if (args.Value.TryGetProperty("chunk_id", out var jChunk) && jChunk.GetString() is string chunkId && !string.IsNullOrEmpty(chunkId))
            {
                if (!MapChunker.TryParseChunkId(chunkId, out int cx, out int cz))
                    return ToolResult.Error($"无效的 chunk_id: {chunkId}");
                if (Find.CurrentMap == null) return ToolResult.Error("当前没有可用地图。");
                var s = RimWorldMCPMod.Instance?.Settings;
                int cw = s?.ChunkWidth ?? 32, ch = s?.ChunkHeight ?? 32;
                var chunk = MapChunker.GetChunkByIndex(cx, cz, Find.CurrentMap.Size.x, Find.CurrentMap.Size.z, cw, ch);
                if (chunk == null) return ToolResult.Error($"Chunk {chunkId} 不存在");
                posX = chunk.MinX; endX = chunk.MaxX;
                posY = chunk.MinZ; endY = chunk.MaxZ;
            }
            else
            {
                if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out posX))
                    return ToolResult.Error("缺少必填参数: pos_x");
                if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out posY))
                    return ToolResult.Error("缺少必填参数: pos_y");
                endX = posX; endY = posY;
                if (args.Value.TryGetProperty("end_x", out var jEx)) jEx.TryGetInt32(out endX);
                if (args.Value.TryGetProperty("end_y", out var jEy)) jEy.TryGetInt32(out endY);
            }

            int minX = Math.Min(posX, endX), maxX = Math.Max(posX, endX);
            int minY = Math.Min(posY, endY), maxY = Math.Max(posY, endY);

            if (maxX - minX > 100 || maxY - minY > 100)
                return ToolResult.Error("范围不能超过 100x100");

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");

                    int mapW = map.Size.x, mapH = map.Size.z;
                    if (minX < 0 || minY < 0 || maxX >= mapW || maxY >= mapH)
                        return ToolResult.Error($"坐标超出地图边界 (0~{mapW - 1}, 0~{mapH - 1})");

                    var sb = new StringBuilder();
                    sb.AppendLine($"## 区域详情 ({minX},{minY}) ~ ({maxX},{maxY})");
                    sb.AppendLine();

                    int buildingCount = 0, itemCount = 0, plantCount = 0, pawnCount = 0;

                    int foggedCells = 0;

                    // 建筑
                    var buildings = new List<string>();
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            var pos = new IntVec3(x, 0, y);
                            if (pos.Fogged(map)) { foggedCells++; continue; }
                            var b = pos.GetEdifice(map);
                            if (b != null)
                            {
                                var stuffLabel = b.Stuff != null ? $" ({b.Stuff.label})" : "";
                                buildings.Add($"- [{x},{y}] {b.Label}{stuffLabel} ({(int)(b.HitPoints * 100f / b.MaxHitPoints)}%, ID:{b.thingIDNumber})");
                                buildingCount++;
                            }
                        }
                    }
                    if (buildings.Count > 0)
                    {
                        sb.AppendLine($"### 建筑 ({buildingCount})");
                        foreach (var line in buildings.Take(50)) sb.AppendLine(line);
                        if (buildings.Count > 50) sb.AppendLine($"... 另有 {buildings.Count - 50} 个");
                        sb.AppendLine();
                    }

                    // 掉落物品
                    var items = new List<string>();
                    for (int y = minY; y <= maxY; y++)
                        for (int x = minX; x <= maxX; x++)
                        {
                            var pos = new IntVec3(x, 0, y);
                            if (pos.Fogged(map)) continue;
                            foreach (var t in pos.GetThingList(map))
                            {
                                if (t.def.category != ThingCategory.Item) continue;
                                var quality = "";
                                if (t.TryGetComp<CompQuality>() != null)
                                {
                                    var qc = t.TryGetComp<CompQuality>();
                                    if (qc != null) quality = $" ({qc.Quality.GetLabel()})";
                                }
                                var label = t.def.IsApparel || t.def.IsWeapon ? $"{t.Label}{quality}" : $"{t.Label} x{t.stackCount}";
                                items.Add($"- [{x},{y}] {label} (ID:{t.thingIDNumber})");
                                itemCount++;
                            }
                        }
                    if (items.Count > 0)
                    {
                        sb.AppendLine($"### 物品 ({itemCount})");
                        foreach (var line in items.Take(50)) sb.AppendLine(line);
                        if (items.Count > 50) sb.AppendLine($"... 另有 {items.Count - 50} 个");
                        sb.AppendLine();
                    }

                    // 植物
                    var plants = new List<string>();
                    for (int y = minY; y <= maxY; y++)
                        for (int x = minX; x <= maxX; x++)
                        {
                            var pos = new IntVec3(x, 0, y);
                            if (pos.Fogged(map)) continue;
                            var p = pos.GetPlant(map);
                            if (p != null)
                            {
                                plants.Add($"- [{x},{y}] {p.Label} (成长 {p.Growth * 100f:F0}%, ID:{p.thingIDNumber})");
                                plantCount++;
                            }
                        }
                    if (plants.Count > 0)
                    {
                        sb.AppendLine($"### 植物 ({plantCount})");
                        foreach (var line in plants.Take(30)) sb.AppendLine(line);
                        if (plants.Count > 30) sb.AppendLine($"... 另有 {plants.Count - 30} 个");
                        sb.AppendLine();
                    }

                    // 生物
                    var pawns = new List<string>();
                    var allPawns = map.mapPawns.AllPawnsSpawned;
                    foreach (var pawn in allPawns)
                    {
                        var p = pawn.Position;
                        if (p.x >= minX && p.x <= maxX && p.z >= minY && p.z <= maxY)
                        {
                            if (pawn.Fogged()) continue;
                            string statusTag = pawn.Downed ? " [倒地]"
                                : IsFleeing(pawn) ? " [逃跑中]"
                                : pawn.Drafted ? " [征召]"
                                : "";
                            pawns.Add($"- [{p.x},{p.z}] {pawn.LabelShort} ({pawn.KindLabel}, ID:{pawn.thingIDNumber}){statusTag}");
                            pawnCount++;
                        }
                    }
                    if (pawns.Count > 0)
                    {
                        sb.AppendLine($"### 生物 ({pawnCount})");
                        foreach (var line in pawns) sb.AppendLine(line);
                        sb.AppendLine();
                    }

                    if (buildingCount == 0 && itemCount == 0 && plantCount == 0 && pawnCount == 0)
                        sb.AppendLine("该区域为空。");
                    if (foggedCells > 0)
                        sb.AppendLine($"（{foggedCells} 格被迷雾覆盖，未显示内容）");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"区域扫描失败: {ex.Message}");
                }
            });
        }

        private static bool IsFleeing(Pawn pawn)
        {
            if (pawn.InMentalState)
            {
                var def = pawn.MentalStateDef;
                if (def == MentalStateDefOf.PanicFlee || def == MentalStateDefOf.PanicFleeFire)
                    return true;
            }
            if (pawn.CurJob?.def == JobDefOf.Flee)
                return true;
            var lord = pawn.GetLord();
            if (lord?.CurLordToil is LordToil_PanicFlee)
                return true;
            return false;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var posX)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var posY)) return null;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var endX)
                && args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var endY))
                return (Math.Min(posX, endX), Math.Min(posY, endY), Math.Max(posX, endX), Math.Max(posY, endY));
            return (posX, posY, posX, posY);
        }
    }
}
