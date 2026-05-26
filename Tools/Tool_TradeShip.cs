using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_TradeShip : ITool
    {
        public string Name => "trade_with_ship";
        public string Description => "与经过的贸易商船交易（虚空贸易）。通过通讯台直接打开交易窗口，无需对话框。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                ship_name = new { type = "string", description = "商船名称（可选，不传则列出可用商船）" },
                colonist_id = new { type = "integer", description = "使用的殖民者 ID（可选，不传则自动选）" }
            },
            required = Array.Empty<string>()
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            string? shipName = null;
            if (args != null && args.Value.TryGetProperty("ship_name", out var jSn))
                shipName = jSn.GetString();

            int? colonistId = null;
            if (args != null && args.Value.TryGetProperty("colonist_id", out var jCid) && jCid.TryGetInt32(out var cid))
                colonistId = cid;

            var capShip = shipName;

            return await McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    Map map = Find.CurrentMap;
                    if (map == null) return ToolResult.Error("没有当前地图");

                    var console = map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>()
                        .FirstOrDefault(c => c.CanUseCommsNow);
                    if (console == null)
                        return ToolResult.Error("没有可用的通讯台（可能无电力或不存在）。建造通讯台并确保有电。");

                    var ships = map.passingShipManager.passingShips.OfType<TradeShip>()
                        .Where(s => !s.Departed && s.CanTradeNow)
                        .ToList();

                    if (ships.Count == 0)
                        return ToolResult.Error("当前没有可贸易的商船经过。等待商船经过殖民地轨道。");

                    if (string.IsNullOrWhiteSpace(capShip))
                    {
                        var list = string.Join(", ", ships.Select(s => $"{s.name} ({s.TraderKind?.label ?? "?"})"));
                        return ToolResult.Success($"当前可贸易商船: {list}\n\n调用 trade_with_ship(ship_name: \"商船名称\") 开始交易。");
                    }

                    var ship = ships.FirstOrDefault(s =>
                        s.name.ToLowerInvariant().Contains(capShip.ToLowerInvariant()));
                    if (ship == null)
                        return ToolResult.Error($"找不到商船: {capShip}。可用: {string.Join(", ", ships.Select(s => s.name))}");

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

                    console.GiveUseCommsJob(pawn, ship);
                    return ToolResult.Success($"{pawn.Name.ToStringShort} 已前往通讯台与 {ship.name} 交易。交易窗口将直接打开。");
                }
                catch (Exception ex) { return ToolResult.Error($"贸易失败: {ex.Message}"); }
            });
        }
        public (int x, int y)? GetTargetPos(JsonElement? args) => null;
    }
}
