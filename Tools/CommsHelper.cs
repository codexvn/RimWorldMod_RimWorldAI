using System;
using System.Linq;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public static class CommsHelper
    {
        public static Task<ToolResult> Execute(string factionName, int? colonistId, string actionLabel)
        {
            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    var faction = Find.FactionManager.AllFactionsVisible
                        .FirstOrDefault(f => f.Name.ToLowerInvariant().Contains(factionName.ToLowerInvariant())
                            && !f.IsPlayer && !f.temporary);
                    if (faction == null)
                    {
                        var available = string.Join(", ", Find.FactionManager.AllFactionsVisible
                            .Where(f => !f.IsPlayer && !f.temporary).Select(f => f.Name));
                        return ToolResult.Error($"找不到派系: {factionName}。可用: {available}");
                    }

                    var console = map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>()
                        .FirstOrDefault(c => c.CanUseCommsNow);
                    if (console == null)
                        return ToolResult.Error("没有可用的通讯台（可能无电力或不存在）");

                    Pawn pawn;
                    if (colonistId.HasValue)
                    {
                        pawn = PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault(c => c.thingIDNumber == colonistId.Value);
                        if (pawn == null) return ToolResult.Error($"找不到殖民者 ID={colonistId}");
                    }
                    else
                    {
                        pawn = PawnsFinder.AllMaps_FreeColonistsSpawned
                            .Where(p => p.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                            .Where(p => p.CanReach(console, PathEndMode.InteractionCell, Danger.Some, false, false, TraverseMode.ByPawn))
                            .OrderBy(p => p.Position.DistanceToSquared(console.Position))
                            .FirstOrDefault();
                        if (pawn == null) return ToolResult.Error("没有空闲且能说话的殖民者可到达通讯台");
                    }

                    if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法说话，无法使用通讯台");
                    if (!pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some, false, false, TraverseMode.ByPawn))
                        return ToolResult.Error($"{pawn.Name.ToStringShort} 无法到达通讯台");

                    console.GiveUseCommsJob(pawn, faction);
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往通讯台联系 {faction.Name}（{actionLabel}）。对话打开后可用 select_dialog_option 选择。");
                }
                catch (Exception ex) { return ToolResult.Error($"通讯失败: {ex.Message}"); }
            });
        }
    }
}
