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
    public class Tool_DesignateHunt : ITool, IRequiresAdvanceTick
    {
        public string Name => "designate_hunt";
        public string Description => "标记野生动物狩猎。支持按坐标区域标记或按 ID 指定单个动物。危险动物（掠食者、狂暴、兽疫）默认跳过。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                pos_x = new { type = "integer", description = "左下 X 坐标（区域模式，与 target_id 二选一）" },
                pos_y = new { type = "integer", description = "左下 Y 坐标（区域模式，与 target_id 二选一）" },
                end_x = new { type = "integer", description = "右上 X 坐标（可选）" },
                end_y = new { type = "integer", description = "右上 Y 坐标（可选）" },
                target_id = new { type = "integer", description = "目标动物 thingIDNumber（来自 find_pawn，与 pos_x/pos_y 二选一）" },
                allow_dangerous = new { type = "boolean", description = "是否允许狩猎危险动物（掠食者/狂暴/兽疫），默认 false", @default = false }
            },
            required = new[] { "pos_x", "pos_y", "target_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");

            int targetId = 0;
            bool hasTargetId = args.Value.TryGetProperty("target_id", out var jTid) && jTid.TryGetInt32(out targetId);
            bool allowDangerous = args.Value.TryGetProperty("allow_dangerous", out var jAd) && jAd.ValueKind == JsonValueKind.True;

            // 单目标模式
            if (hasTargetId)
            {
                return await McpCommandQueue.DispatchAsync(() =>
                {
                    var map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("当前没有可用地图。");
                    var target = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == targetId);
                    if (target == null) return ToolResult.Error($"找不到目标 ID={targetId}");
                    if (!target.AnimalOrWildMan() || target.IsPrisonerInPrisonCell())
                        return ToolResult.Error($"{target.LabelShort} 不是野生动物。");
                    if (target.Faction?.def.humanlikeFaction == true)
                        return ToolResult.Error($"{target.LabelShort} 不属于野生动物。");
                    if (!allowDangerous && IsDangerousAnimal(target, out var dangerReason))
                        return ToolResult.Error($"{target.LabelShort} 是危险动物（{dangerReason}）。如需狩猎请传 allow_dangerous=true。");
                    map.designationManager.RemoveAllDesignationsOn(target, false);
                    map.designationManager.AddDesignation(new Designation(target, DesignationDefOf.Hunt));
                    return ToolResult.Success($"已标记 {target.LabelShort} 为狩猎目标。");
                });
            }

            // 区域模式
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var startX))
                return ToolResult.Error("缺少 pos_x");
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var startY))
                return ToolResult.Error("缺少 pos_y");

            int endX = startX, endY = startY;
            if (args.Value.TryGetProperty("end_x", out var jEx) && jEx.TryGetInt32(out var ex)) endX = ex;
            if (args.Value.TryGetProperty("end_y", out var jEy) && jEy.TryGetInt32(out var ey)) endY = ey;

            int minX = Math.Min(startX, endX), maxX = Math.Max(startX, endX);
            int minZ = Math.Min(startY, endY), maxZ = Math.Max(startY, endY);

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return ToolResult.Error("当前没有可用地图。");
                int count = 0;
                var names = new List<string>();
                var skippedDangerous = new List<string>();
                for (int x = minX; x <= maxX; x++)
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (!cell.InBounds(map) || cell.Fogged(map)) continue;
                        foreach (var t in cell.GetThingList(map))
                        {
                            if (t is not Pawn pawn) continue;
                            if (!pawn.AnimalOrWildMan() || pawn.IsPrisonerInPrisonCell()) continue;
                            if (pawn.Faction?.def.humanlikeFaction == true) continue;
                            if (!allowDangerous && IsDangerousAnimal(pawn, out var reason))
                            { skippedDangerous.Add($"{pawn.LabelShort}({reason})"); continue; }
                            if (map.designationManager.DesignationOn(pawn, DesignationDefOf.Hunt) != null) continue;
                            map.designationManager.RemoveAllDesignationsOn(pawn, false);
                            map.designationManager.AddDesignation(new Designation(pawn, DesignationDefOf.Hunt));
                            count++;
                            names.Add(pawn.LabelShort);
                        }
                    }
                var sb = new StringBuilder();
                sb.Append(count > 0
                    ? $"已标记 {count} 个狩猎目标: {string.Join(", ", names)}"
                    : "区域内没有可狩猎的动物。");
                if (skippedDangerous.Count > 0)
                    sb.Append($"（跳过 {skippedDangerous.Count} 只危险动物: {string.Join(", ", skippedDangerous)}）");
                return ToolResult.Success(sb.ToString());
            });
        }

        private static bool IsDangerousAnimal(Pawn pawn, out string reason)
        {
            if (pawn.RaceProps?.predator == true) { reason = "掠食者"; return true; }
            if (pawn.InAggroMentalState) { reason = "攻击性"; return true; }
            if (pawn.health?.hediffSet?.HasHediff(HediffDefOf.Scaria) == true) { reason = "兽疫"; return true; }
            reason = "";
            return false;
        }

        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args)
        {
            if (args == null) return null;
            if (args.Value.TryGetProperty("target_id", out var jTid) && jTid.TryGetInt32(out var tid))
            {
                var map = Find.CurrentMap;
                if (map == null) return null;
                var p = CameraHelper.FindPawnById(map, tid);
                if (p == null) return null;
                return (p.Position.x - 2, p.Position.z - 2, p.Position.x + 2, p.Position.z + 2);
            }
            if (!args.Value.TryGetProperty("pos_x", out var jX) || !jX.TryGetInt32(out var x)) return null;
            if (!args.Value.TryGetProperty("pos_y", out var jY) || !jY.TryGetInt32(out var y)) return null;
            int ex = x, ey = y;
            if (args.Value.TryGetProperty("end_x", out var jEX) && jEX.TryGetInt32(out var _ex)) ex = _ex;
            if (args.Value.TryGetProperty("end_y", out var jEY) && jEY.TryGetInt32(out var _ey)) ey = _ey;
            return (Math.Min(x, ex), Math.Min(y, ey), Math.Max(x, ex), Math.Max(y, ey));
        }
    }
}
