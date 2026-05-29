using UnityEngine;
using Verse;
using RimWorld;

namespace RimWorldMCP.Helpers
{
    public static class GameTimeHelper
    {
        /// <summary>将绝对 Tick 格式化为本地化游戏日期字符串（中文：翠象 第2天, 5500年）</summary>
        public static string FormatGameTime(int tick)
        {
            var loc = Vector2.zero;
            var map = Find.CurrentMap;
            if (map != null)
                loc = Find.WorldGrid.LongLatOf(map.Tile);
            return GenDate.DateFullStringWithHourAt((long)tick, loc);
        }

        /// <summary>当前游戏时间的本地化字符串</summary>
        public static string CurrentTime()
        {
            return FormatGameTime(Find.TickManager.TicksAbs);
        }
    }
}
