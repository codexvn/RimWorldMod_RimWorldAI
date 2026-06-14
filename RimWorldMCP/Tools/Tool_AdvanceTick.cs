using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using RimWorld;
using RimWorldMCP.Harmony;
using Verse;

namespace RimWorldMCP.Tools
{
    public class Tool_AdvanceTick : ITool, INoMapRequired
    {
        public string Name => "advance_tick";
        public string Description => "以最快速度推进游戏指定 tick 数后恢复原速度。2500 tick = 1 游戏小时，最快约 0.6 秒。支持小数（如 0.5 = 半小时）。和平时期用 12 小时大步推进。Plan/Act 阶段为受控 Advance 推进（完成后自动恢复强制暂停）。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                hours = new { type = "number", description = "要运行的游戏内小时数（备选）。1 小时 = 2500 tick。推荐 0.5~4 小时，和平时期用 2~4 小时大步推进。" },
                ticks = new { type = "integer", description = "要运行的 tick 数（主参数，0~60000）。2500 tick = 1 游戏小时。优先于 hours。" }
            }
        });

        // pending: targetTick → (TCS, savedSpeed, initialIdleIds, battleLogStartTick)
        private static readonly Dictionary<int, (TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed, HashSet<int> initialIdleIds, int battleLogStartTick)> _pending = new();
        private static readonly object _lock = new();

        /// <summary>是否有等待中的 tick advance（AutoPauseGuard 据此跳过自动暂停）</summary>
        public static bool IsActive
        {
            get { lock (_lock) return _pending.Count > 0; }
        }

        /// <summary>取消所有等待中的 advance_tick（中断按钮 / 玩家手动暂停触发）</summary>
        public static void CancelAll()
        {
            List<(TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed, HashSet<int> idleIds, int battleLogStart)> cancelled;
            lock (_lock)
            {
                cancelled = new List<(TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed, HashSet<int> idleIds, int battleLogStart)>(_pending.Values);
                _pending.Clear();
            }
            // Advance 模式下：恢复暂停
            if (GamePaceEnforcer.AdvanceTargetTick > 0)
            {
                GamePaceEnforcer.CompleteAdvance();
            }
            else
            {
                var tm = Find.TickManager;
                var restoreSpeed = cancelled.Count > 0 ? cancelled[0].savedSpeed : TimeSpeed.Superfast;
                if (tm != null) tm.CurTimeSpeed = restoreSpeed;
            }
            foreach (var (tcs, _, _, _) in cancelled)
                tcs.TrySetResult(ToolResult.Success("advance_tick 已被中断，已恢复原速度。"));
        }

        /// <summary>每帧主线程调用</summary>
        public static void ProcessPending()
        {
            var tickManager = Find.TickManager;
            if (tickManager == null) return;
            int current = tickManager.TicksGame;

            List<(int target, TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed, HashSet<int> _, int battleLogStart)>? completed = null;
            bool interrupted = false;
            string interruptReason = "";
            TimeSpeed? savedSpeed = null;

            bool highDanger = NotificationBus.HighDangerPending;
            if (highDanger) NotificationBus.HighDangerPending = false;

            bool colonistsIdle = false;
            if (!highDanger && !tickManager.Paused)
            {
                var map = Find.CurrentMap;
                if (map != null && map.IsPlayerHome)
                {
                    var seenIds = new HashSet<int>();
                    foreach (var kv in _pending)
                        seenIds.UnionWith(kv.Value.initialIdleIds);
                    colonistsIdle = map.mapPawns.FreeColonistsSpawned
                        .Any(p => p.mindState.IsIdle && !p.IsQuestLodger() && !p.Drafted
                            && !seenIds.Contains(p.thingIDNumber));
                }
            }

            // 检查 Advance 是否已到达目标
            if (GamePaceEnforcer.IsAdvanceComplete(current))
            {
                lock (_lock)
                {
                    if (_pending.Count > 0)
                    {
                        completed = _pending.Select(kv => (kv.Key, kv.Value.tcs, kv.Value.savedSpeed, kv.Value.initialIdleIds, kv.Value.battleLogStartTick)).ToList();
                        if (completed.Count > 0) savedSpeed = completed[0].savedSpeed;
                        _pending.Clear();
                    }
                }
                if (completed != null)
                {
                    GamePaceEnforcer.CompleteAdvance();
                    foreach (var (_, tcs, _, _, battleLogStart) in completed)
                        tcs.TrySetResult(ToolResult.Success(BuildGameStatus() + "\n\nadvance_tick 推进完成。" + BuildBattleReport(battleLogStart)));
                }
                return;
            }

            lock (_lock)
            {
                if (_pending.Count == 0) return;

                if (tickManager.Paused)
                {
                    interrupted = true;
                    interruptReason = "玩家手动暂停";
                    completed = _pending.Select(kv => (kv.Key, kv.Value.tcs, kv.Value.savedSpeed, kv.Value.initialIdleIds, kv.Value.battleLogStartTick)).ToList();
                    if (completed.Count > 0) savedSpeed = completed[0].savedSpeed;
                    _pending.Clear();
                }
                else if (highDanger || colonistsIdle)
                {
                    interrupted = true;
                    interruptReason = highDanger ? "高危事件触发" : "殖民者出现空闲";
                    completed = _pending.Select(kv => (kv.Key, kv.Value.tcs, kv.Value.savedSpeed, kv.Value.initialIdleIds, kv.Value.battleLogStartTick)).ToList();
                    if (completed.Count > 0) savedSpeed = completed[0].savedSpeed;
                    _pending.Clear();
                }
                else
                {
                    var reached = new List<(int target, TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed, HashSet<int> idleIds, int battleLogStart)>();
                    foreach (var kv in _pending)
                        if (current >= kv.Key)
                            reached.Add((kv.Key, kv.Value.tcs, kv.Value.savedSpeed, kv.Value.initialIdleIds, kv.Value.battleLogStartTick));
                    foreach (var r in reached) _pending.Remove(r.target);
                    if (reached.Count > 0)
                    {
                        completed = reached;
                        savedSpeed = reached[0].savedSpeed;
                    }
                }
            }

            if (completed == null) return;

            if (interrupted)
            {
                if (GamePaceEnforcer.AdvanceTargetTick > 0)
                    GamePaceEnforcer.CompleteAdvance();
                else if (savedSpeed.HasValue)
                {
                    var tm = Find.TickManager;
                    if (tm != null) tm.CurTimeSpeed = savedSpeed.Value;
                }
                foreach (var (_, tcs, _, _, battleLogStart) in completed)
                    tcs.TrySetResult(ToolResult.Success($"advance_tick 已中断: {interruptReason}。\n\n{BuildGameStatus()}" + BuildBattleReport(battleLogStart)));
            }
            else
            {
                if (GamePaceEnforcer.AdvanceTargetTick > 0)
                    GamePaceEnforcer.CompleteAdvance();
                else if (savedSpeed.HasValue)
                {
                    var tm = Find.TickManager;
                    if (tm != null) tm.CurTimeSpeed = savedSpeed.Value;
                }
                foreach (var (_, tcs, _, _, battleLogStart) in completed)
                    tcs.TrySetResult(ToolResult.Success(BuildGameStatus() + "\n\nadvance_tick 推进完成。" + BuildBattleReport(battleLogStart)));
            }
        }

        private static string BuildBattleReport(int battleLogStartTick)
        {
            try
            {
                int sinceTick = battleLogStartTick > 0 ? battleLogStartTick : (Find.TickManager?.TicksGame ?? 0);
                var summaries = BattleLogCollector.Collect(sinceTick, Find.TickManager?.TicksGame ?? sinceTick + 1);
                BattleLogCollector.PushAll(summaries);
                return BattleLogCollector.BuildTextReport(summaries);
            }
            catch (Exception ex) { McpLog.Warn($"[AdvanceTick] 收集战斗日志失败: {ex.Message}"); return ""; }
        }

        private static string BuildGameStatus()
        {
            var map = Find.CurrentMap;
            var sb = new StringBuilder();
            sb.AppendLine($"## 游戏状态 (Tick {Find.TickManager?.TicksGame ?? 0})");

            if (map == null) { sb.AppendLine("当前无地图。"); return sb.ToString(); }

            var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
            sb.AppendLine($"- 殖民者: {colonists.Count} 人");

            int enemies = map.mapPawns.AllPawnsSpawned.Count(p => p.HostileTo(Faction.OfPlayer) && !p.Fogged());
            if (enemies > 0) sb.AppendLine($"- 敌人: {enemies}");

            int idle = 0, sleeping = 0;
            foreach (var c in colonists)
            {
                if (c.mindState.IsIdle) idle++;
                if (!c.Awake()) sleeping++;
            }
            if (idle > 0) sb.AppendLine($"- 空闲: {idle} 人");
            if (sleeping > 0) sb.AppendLine($"- 睡眠: {sleeping} 人");

            return sb.ToString();
        }

        // ============== 低速检测 ==============

        private static double _lowSpeedSinceReal;
        private static bool _lowSpeedWarningReady;
        private const double LowSpeedThresholdSec = 30.0;

        /// <summary>每帧检测：运行中但低于 3 倍速持续超阈值则标记通知</summary>
        public static void LowSpeedTick()
        {
            var tm = Find.TickManager;
            if (tm == null || tm.Paused || _lowSpeedWarningReady) return;

            if (EnemyOnMap()) { _lowSpeedSinceReal = 0; return; }

            bool isBelowSuperfast = tm.CurTimeSpeed < TimeSpeed.Superfast;
            if (isBelowSuperfast)
            {
                var now = Time.realtimeSinceStartupAsDouble;
                if (_lowSpeedSinceReal <= 0)
                    _lowSpeedSinceReal = now;
                else if (now - _lowSpeedSinceReal >= LowSpeedThresholdSec)
                    _lowSpeedWarningReady = true;
            }
            else
            {
                _lowSpeedSinceReal = 0;
            }
        }

        private static bool EnemyOnMap()
        {
            var map = Find.CurrentMap;
            return map != null && map.mapPawns.AllPawnsSpawned.Any(p => p.HostileTo(Faction.OfPlayer) && !p.Fogged() && !p.Dead);
        }

        /// <summary>工具调用结束时取警告并重置（仅通知一次）</summary>
        public static string? GetLowSpeedWarning()
        {
            if (!_lowSpeedWarningReady) return null;
            _lowSpeedWarningReady = false;
            _lowSpeedSinceReal = 0;
            return "游戏长时间以低于 3 倍速运行（超 30 秒），建议用 toggle_pause(speed=\"superfast\") 恢复 3 倍速或 advance_tick 快速推进。";
        }

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null) return ToolResult.Error("缺少参数");
            int ticks = 0;
            if (args.Value.TryGetProperty("ticks", out var jTicks) && jTicks.TryGetInt32(out var t))
                ticks = t;
            else if (args.Value.TryGetProperty("hours", out var jHours))
            {
                double hours = jHours.ValueKind == JsonValueKind.Number ? jHours.GetDouble() : 0;
                if (hours > 0) ticks = (int)Math.Round(hours * 2500);
            }
            if (ticks <= 0) return ToolResult.Error("ticks 或 hours 必须 > 0");
            if (ticks > 60000) return ToolResult.Error("ticks 过大，单次最多 60000 tick（24 游戏小时）");

            if (LongEventHandler.ForcePause)
                return ToolResult.Error("游戏正在加载中，主线程暂时不可用，请稍后重试。");

            var tcs = new TaskCompletionSource<ToolResult>();

            await McpCommandQueue.DispatchAsync<object>(() =>
            {
                var tm = Find.TickManager;
                if (tm == null)
                {
                    tcs.TrySetResult(ToolResult.Error("TickManager 不可用"));
                    return null!;
                }

                int target = tm.TicksGame + ticks;
                var savedSpeed = tm.CurTimeSpeed;

                var idleIds = new HashSet<int>();
                var currentMap = Find.CurrentMap;
                if (currentMap != null)
                {
                    foreach (var p in currentMap.mapPawns.FreeColonistsSpawned)
                        if (p.mindState.IsIdle && !p.IsQuestLodger() && !p.Drafted)
                            idleIds.Add(p.thingIDNumber);
                }

                // Plan/Act 强制暂停模式 → 走 Advance 受控通道
                // 如果游戏被暂停（Plan/Act 模式），走 Advance 受控通道
                if (tm.Paused)
                {
                    GamePaceEnforcer.StartAdvance(target, savedSpeed);
                    tm.TogglePaused();
                }

                int battleLogStart = tm.TicksGame; // 推进开始时记录
                BattleLogCollector.LastCollectTick = battleLogStart;
                lock (_lock) { _pending[target] = (tcs, savedSpeed, idleIds, battleLogStart); }
                tm.CurTimeSpeed = TimeSpeed.Ultrafast;
                return null!;
            });

            return await tcs.Task;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}