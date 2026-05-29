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
    public class Tool_AdvanceTick : ITool
    {
        public string Name => "advance_tick";
        public string Description => "以最快速度推进游戏指定小时数后恢复原速度。1 游戏小时 = 2500 tick，最快约 0.6 秒。支持小数（如 0.5 = 半小时）。和平时期用 12 小时大步推进。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                hours = new { type = "number", description = "要运行的游戏内小时数。1 小时 = 2500 tick，超快模式下约 0.8 秒。支持小数，如 0.5 = 半小时。推荐 0.5~4 小时，和平时期用 2~4 小时大步推进。" }
            },
            required = new[] { "hours" }
        });

        // pending: targetTick → (TCS, savedSpeed)
        private static readonly Dictionary<int, (TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed)> _pending = new();
        private static readonly object _lock = new();

        /// <summary>是否有等待中的 tick advance（AutoPauseGuard 据此跳过自动暂停）</summary>
        public static bool IsActive
        {
            get { lock (_lock) return _pending.Count > 0; }
        }

        /// <summary>取消所有等待中的 advance_tick（中断按钮 / 玩家手动暂停触发）</summary>
        public static void CancelAll()
        {
            List<(TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed)> cancelled;
            lock (_lock)
            {
                cancelled = new List<(TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed)>(_pending.Values);
                _pending.Clear();
            }
            var tm = Find.TickManager;
            // 恢复到最早保存的速度，没有则用 3 倍速
            var restoreSpeed = cancelled.Count > 0 ? cancelled[0].savedSpeed : TimeSpeed.Superfast;
            if (tm != null) tm.CurTimeSpeed = restoreSpeed;
            foreach (var (tcs, _) in cancelled)
                tcs.TrySetResult(ToolResult.Success("advance_tick 已被中断，已恢复原速度。"));
        }

        /// <summary>每帧主线程调用</summary>
        public static void ProcessPending()
        {
            var tickManager = Find.TickManager;
            if (tickManager == null) return;
            int current = tickManager.TicksGame;

            List<(int target, TaskCompletionSource<ToolResult> tcs, TimeSpeed savedSpeed)>? completed = null;
            bool playerPaused = false;
            TimeSpeed? savedSpeed = null;

            // 高危事件 → 立即暂停，提前退出
            bool highDanger = NotificationBus.HighDangerPending;
            if (highDanger) NotificationBus.HighDangerPending = false;

            lock (_lock)
            {
                if (_pending.Count == 0) return;

                // 玩家按空格暂停 → 中断所有等待
                if (tickManager.Paused)
                {
                    playerPaused = true;
                    completed = _pending.Select(kv => (kv.Key, kv.Value.tcs, kv.Value.savedSpeed)).ToList();
                    if (completed.Count > 0) savedSpeed = completed[0].savedSpeed;
                    _pending.Clear();
                }
                else if (highDanger)
                {
                    completed = _pending.Select(kv => (kv.Key, kv.Value.tcs, kv.Value.savedSpeed)).ToList();
                    if (completed.Count > 0) savedSpeed = completed[0].savedSpeed;
                    _pending.Clear();
                }
                else
                {
                    foreach (var kv in _pending)
                    {
                        if (current >= kv.Key)
                        {
                            completed ??= new List<(int, TaskCompletionSource<ToolResult>, TimeSpeed)>();
                            completed.Add((kv.Key, kv.Value.tcs, kv.Value.savedSpeed));
                        }
                    }
                    if (completed != null)
                    {
                        foreach (var (target, _, _) in completed)
                            _pending.Remove(target);
                        if (completed.Count > 0) savedSpeed = completed[0].savedSpeed;
                    }
                }
            }

            if (completed != null)
            {
                tickManager.CurTimeSpeed = savedSpeed ?? TimeSpeed.Superfast;

                if (playerPaused)
                {
                    foreach (var (_, tcs, _) in completed)
                        tcs.TrySetResult(ToolResult.Success("advance_tick 已被中断（玩家暂停），已恢复原速度。"));
                }
                else if (highDanger)
                {
                    var dangerList = NotificationBus.Drain();
                    var sb = new StringBuilder();
                    sb.AppendLine("## 紧急事件！advance_tick 提前退出");
                    foreach (var n in dangerList)
                        sb.AppendLine($"- {n.Label}");
                    var result = ToolResult.Success(sb.ToString());
                    foreach (var (_, tcs, _) in completed)
                        tcs.TrySetResult(result);
                }
                else
                {
                    var status = BuildGameStatus();
                    foreach (var (_, tcs, _) in completed)
                        tcs.TrySetResult(ToolResult.Success(status));
                }
            }
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

            int idle = 0;
            foreach (var c in colonists)
                if (c.mindState.IsIdle) idle++;
            if (idle > 0) sb.AppendLine($"- 空闲: {idle} 人");

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

            // 有敌人时重置计时（战斗场景不应催促加速）
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
            if (!args.Value.TryGetProperty("hours", out var jHours))
                return ToolResult.Error("缺少必填参数: hours");
            double hours = jHours.ValueKind == JsonValueKind.Number ? jHours.GetDouble() : 0;
            if (hours <= 0) return ToolResult.Error("hours 必须 > 0");
            if (hours > 24) return ToolResult.Error("hours 过大，单次最多 24 小时（1 天）");
            int ticks = (int)Math.Round(hours * 2500);

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
                lock (_lock) { _pending[target] = (tcs, savedSpeed); }
                tm.CurTimeSpeed = TimeSpeed.Ultrafast;
                return null!;
            });

            return await tcs.Task;
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
