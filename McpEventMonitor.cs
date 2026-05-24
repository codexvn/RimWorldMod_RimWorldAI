using System.Linq;
using Verse;
using RimWorld;

namespace RimWorldMCP
{
    public static class McpEventMonitor
    {
        private static int _nextCheckTick;
        private const int CheckIntervalTicks = 120;
        private static int _lastColonistCount = -1;
        private static int _lastIdleCount = -1;
        private static bool _lastRaidActive;
        private static bool _lastFireActive;

        public static void Tick()
        {
            if (!McpClient.IsConnected) return;
            var tick = Find.TickManager?.TicksGame ?? 0;
            if (tick < _nextCheckTick) return;
            _nextCheckTick = tick + CheckIntervalTicks;

            var map = Find.CurrentMap;
            if (map == null) return;

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            int colonistCount = colonists.Count;

            if (map.attackTargetsCache?.TargetsHostileToFaction(Faction.OfPlayer)?.Any() == true && !_lastRaidActive)
            {
                Send($"⚠ 袭击！{colonistCount} 名殖民者需要立即征召防御！");
            }
            _lastRaidActive = map.attackTargetsCache?.TargetsHostileToFaction(Faction.OfPlayer)?.Any() ?? false;

            bool fireActive = map.listerThings.ThingsInGroup(ThingRequestGroup.Fire).Count > 0;
            if (fireActive && !_lastFireActive)
            {
                Send("⚠ 火灾！地图上有火势蔓延，立即派遣灭火！");
            }
            _lastFireActive = fireActive;

            int idleCount = colonists.Count(c =>
                (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                && !c.Downed && !c.Deathresting);
            if (idleCount > _lastIdleCount && idleCount > 0)
            {
                var names = colonists
                    .Where(c => (c.CurJob?.def?.defName == "Wait_MaintainPosture" || c.CurJob == null)
                        && !c.Downed && !c.Deathresting)
                    .Take(5).Select(c => c.Name.ToStringShort);
                Send($"{(idleCount > 1 ? $"{idleCount} 名" : "")}殖民者空闲: {string.Join(", ", names)}");
            }
            _lastIdleCount = idleCount;

            if (colonistCount != _lastColonistCount && _lastColonistCount >= 0)
            {
                int diff = colonistCount - _lastColonistCount;
                Send($"殖民者数量变化: {_lastColonistCount} → {colonistCount} ({(diff > 0 ? "+" : "")}{diff})");
            }
            _lastColonistCount = colonistCount;
        }

        private static void Send(string message)
        {
            _ = McpClient.SendMessage(message);
            McpLog.Info($"[event] {message}");
        }
    }
}
