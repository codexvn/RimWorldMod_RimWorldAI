using Verse;

namespace RimWorldMCP
{
    /// <summary>
    /// Advance 受控推进辅助。
    /// advance_tick 在 Plan/Act 强制暂停模式下通过此类临时放行游戏。
    /// </summary>
    public static class GamePaceEnforcer
    {
        /// <summary>当前 Advance 推进的目标 tick，0 表示不在 Advance 阶段</summary>
        public static volatile int AdvanceTargetTick;

        /// <summary>当前 Advance 推进的原始速度（完成后恢复用）</summary>
        public static TimeSpeed AdvanceOriginalSpeed;

        private static readonly object _lock = new();

        /// <summary>开始 Advance 受控推进</summary>
        public static void StartAdvance(int targetTick, TimeSpeed originalSpeed)
        {
            lock (_lock)
            {
                AdvanceTargetTick = targetTick;
                AdvanceOriginalSpeed = originalSpeed;
            }
            McpLog.Info($"[GamePaceEnforcer] Advance 推进开始: targetTick={targetTick}, speed={originalSpeed}");
        }

        /// <summary>Advance 推进完成：恢复原速度并暂停</summary>
        public static void CompleteAdvance()
        {
            lock (_lock)
            {
                AdvanceTargetTick = 0;
                var savedSpeed = AdvanceOriginalSpeed;
                var tm = Find.TickManager;
                if (tm != null)
                {
                    tm.CurTimeSpeed = savedSpeed;
                    if (!tm.Paused)
                        tm.TogglePaused();
                }
                McpLog.Info($"[GamePaceEnforcer] Advance 推进完成，暂停，速度={savedSpeed}");
            }
        }

        /// <summary>每帧检查：Advance 推进是否已到达目标</summary>
        public static bool IsAdvanceComplete(int currentTick)
        {
            return AdvanceTargetTick > 0 && currentTick >= AdvanceTargetTick;
        }
    }
}