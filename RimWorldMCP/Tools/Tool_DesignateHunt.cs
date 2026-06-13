using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_DesignateHunt : ITool, IRequiresAdvanceTick
    {
        public string Name => "designate_hunt";
        public string Description => "标记指定动物为狩猎目标（通过 ID）。先用 find_pawn(kind:\"animal\") 查看可狩猎动物及反击概率，再选择目标。危险动物（掠食者、狂暴、兽疫）默认跳过。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                target_id = new { type = "integer", description = "目标动物 thingIDNumber（来自 find_pawn）" },
                allow_dangerous = new { type = "boolean", description = "是否允许狩猎危险动物（掠食者/狂暴/兽疫），默认 false", @default = false }
            },
            required = new[] { "target_id" }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            if (!args.Value.TryGetProperty("target_id", out var jTid) || !jTid.TryGetInt32(out var targetId))
                return ToolResult.Error("缺少必填参数: target_id（来自 find_pawn 的 thingIDNumber）");
            bool allowDangerous = args.Value.TryGetProperty("allow_dangerous", out var jAd) && jAd.ValueKind == JsonValueKind.True;

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
                float wildness = 0f;
                try { wildness = target.GetStatValue(StatDefOf.Wildness, true, -1); } catch (Exception ex) { McpLog.Info($"[designate_hunt] 读取野性失败: {ex.Message}"); }
                var risk = wildness > 0 ? $"（反击概率 {wildness * 100:F0}%）" : "";
                return ToolResult.Success($"已标记 {target.LabelShort}{risk} 为狩猎目标。");
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
            return null;
        }
    }
}
