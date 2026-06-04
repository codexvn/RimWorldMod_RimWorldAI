using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RimWorldMCP.Constants;
using Verse;

namespace RimWorldMCP.Harmony
{
    /// <summary>事件路由目标</summary>
    public enum EventRoute { Overseer, Economy, Combat, Medic, All, None }

    /// <summary>统一事件模型（原 AgentRuntime.ColonyEvent，内联至此）</summary>
    public class ColonyEvent
    {
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Summary { get; set; } = "";
        public object? Payload { get; set; }
        public int Tick { get; set; }
    }

    /// <summary>自有警报数据（拷贝，不持游戏对象引用）</summary>
    public class AlertInfo
    {
        public string Key { get; set; } = string.Empty;       // 类型名，如 "Alert_NeedDoctor"
        public string Label { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string?[] Culprits { get; set; } = System.Array.Empty<string?>();
    }

    /// <summary>事件等级 — 决定暂停/注入/忽略</summary>
    public enum EventLevel
    {
        Silent = 0,   // 不推送也不注入
        Info = 1,     // 注入工具结果
        Warning = 2,  // 注入工具结果（稍优先）
        Critical = 3  // 暂停游戏
    }

    public static class NotificationBus
    {
        // 待推送通知队列
        public static readonly ConcurrentQueue<Notification> Pending = new();

        // 自有警报镜像：类型名 → 拷贝的警报数据（不持游戏对象引用）
        private static readonly Dictionary<string, AlertInfo> ActiveAlerts = new();

        // Letter 去重（容量上限 5000）
        private const int MaxNotifiedLetters = 5000;
        private static readonly HashSet<int> NotifiedLetters = new();

        // Message 去重（容量上限 2000）
        private const int MaxNotifiedMessages = 2000;
        private static readonly HashSet<string> NotifiedMessages = new();

        /// <summary>事件通知标记 — BridgeLifecycle.CCEventTick() 每帧检查 (L3 Critical)</summary>
        public static volatile bool HighDangerPending;

        // ========== 供 Patch 调用 ==========

        public static void Enqueue(Notification n)
        {
            Pending.Enqueue(n);
            McpLog.Info($"[notify] + {n.Type} danger={n.DangerLabel} pri={n.Priority} label={n.Label}");
            if (!HighDangerPending && GetEventLevel(n.Type, n.DangerLabel, n.Priority) == EventLevel.Critical)
                HighDangerPending = true;

            // 推送到 Agent 侧（不含路由，由 Agent 侧 RouteEvent 决策）
            var level = GetEventLevel(n.Type, n.DangerLabel, n.Priority);

            var evt = new ColonyEvent
            {
                Category = MapCategory(n.Type, n.DangerLabel),
                Severity = level switch
                {
                    EventLevel.Critical => "Critical",
                    EventLevel.Warning => "Warning",
                    EventLevel.Silent => "Silent",
                    _ => "Info"
                },
                Summary = n.Type switch
                {
                    NotificationType.Letter => !string.IsNullOrEmpty(n.Text) && n.Text != n.Label
                        ? $"{n.DangerLabel} | {n.Label} — {n.Text}"
                        : $"{n.DangerLabel} | {n.Label}",
                    NotificationType.Message => $"{n.DangerLabel} | {n.Text}",
                    NotificationType.AlertStart => n.Culprits is { Count: > 0 }
                        ? $"[{n.PriorityLabel}] {n.Label} ({string.Join("、", n.Culprits.Take(3))})"
                        : $"[{n.PriorityLabel}] {n.Label}",
                    NotificationType.AlertEnd => $"解除: {n.Label}",
                    _ => n.Label ?? n.Text ?? ""
                },
                Tick = n.Tick
            };

            try
            {
                var sseJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "event",
                    evt.Category,
                    evt.Severity,
                    evt.Summary,
                    evt.Tick,
                    level = (int)level
                });
                var host = McpServiceManager.Host;
                if (host != null)
                {
                    host.SendEvent(McpChannels.GameNotification, sseJson);
                    McpLog.Info($"[NotificationBus] SSE 已推送: type={n.Type} level={level}");
                }
                else
                {
                    McpLog.Warn($"[NotificationBus] SSE 推送跳过: McpServiceManager.Host 为 null (MCP服务未启动)");
                }
            }
            catch (Exception ex) { McpLog.Warn($"[NotificationBus] SSE 推送失败: {ex.Message}"); }
        }

        /// <summary>事件等级判定 — 统一入口</summary>
        public static EventLevel GetEventLevel(NotificationType type, string dangerLabel, int priority = 0)
        {
            switch (type)
            {
                case NotificationType.Letter:
                    return dangerLabel switch
                    {
                        "大威胁" or "小威胁" or "死亡" or "Boss" or "负面" => EventLevel.Critical,
                        "仪式失败" => EventLevel.Warning,
                        "选择角色" or "游戏结束" or "捆绑" => EventLevel.Silent,
                        _ => EventLevel.Info  // 正面, 事件, 来人, 成长, 任务, 仪式成功
                    };
                case NotificationType.Message:
                    return dangerLabel switch
                    {
                        "大威胁" or "小威胁" or "角色死亡" or "健康事件" or "负面" or "游戏减速" or "被困-紧急" => EventLevel.Critical,
                        "警告" or "被困" => EventLevel.Warning,
                        "拒绝" or "静默" => EventLevel.Silent,
                        _ => EventLevel.Info  // 事件, 正面, 完成, 状态解除
                    };
                case NotificationType.AlertStart:
                    return priority switch
                    {
                        2 => EventLevel.Critical,   // RimWorld AlertPriority.Critical
                        1 => EventLevel.Warning,    // AlertPriority.High
                        _ => EventLevel.Info        // Medium 等不打断
                    };
                case NotificationType.AlertEnd:
                    return EventLevel.Info;
                default:
                    return EventLevel.Info;
            }
        }

        /// <summary>是否高危事件 (L3 Critical)，保持向后兼容</summary>
        internal static bool IsHighDanger(NotificationType type, string dangerLabel, int alertPriority)
        {
            return GetEventLevel(type, dangerLabel) == EventLevel.Critical;
        }

        private static int _lastSpeedSlowdownTick;
        private const int SpeedSlowdownThrottleTicks = 600; // 10 秒内不重复

        /// <summary>游戏速度被强制降低时调用（供 Harmony Patch 使用），10 秒限流。</summary>
        public static void NotifySpeedSlowdown(string reason)
        {
            var tick = Find.TickManager?.TicksGame ?? 0;
            if (tick - _lastSpeedSlowdownTick < SpeedSlowdownThrottleTicks) return;
            _lastSpeedSlowdownTick = tick;

            Pending.Enqueue(new Notification
            {
                Type = NotificationType.Message,
                DangerLabel = "游戏减速",
                Text = reason,
                Tick = tick
            });
            HighDangerPending = true;
        }

        public static bool IsLetterNotified(int letterId) => NotifiedLetters.Contains(letterId);

        public static void MarkLetterNotified(int letterId)
        {
            if (NotifiedLetters.Count >= MaxNotifiedLetters)
                NotifiedLetters.Clear();
            NotifiedLetters.Add(letterId);
        }

        public static bool IsMessageNotified(string loadId) => NotifiedMessages.Contains(loadId);

        public static void MarkMessageNotified(string loadId)
        {
            if (NotifiedMessages.Count >= MaxNotifiedMessages)
                NotifiedMessages.Clear();
            NotifiedMessages.Add(loadId);
        }

        /// <summary>警报变为活跃：存入镜像（不持 Alert 引用）。</summary>
        public static void OnAlertStarted(string key, string label, int priority, string?[] culprits)
        {
            ActiveAlerts[key] = new AlertInfo { Key = key, Label = label, Priority = priority, Culprits = culprits };
            McpLog.Info($"[notify] alert+ pri={priority} key={key} label={label}");
        }

        /// <summary>警报解除：从镜像移除。</summary>
        public static void OnAlertEnded(string key)
        {
            ActiveAlerts.Remove(key);
            McpLog.Info($"[notify] alert- key={key}");
        }

        /// <summary>获取上次解禁警报的标签。</summary>
        public static string? GetAlertLabel(string key)
        {
            return ActiveAlerts.TryGetValue(key, out var info) ? info.Label : null;
        }

        // ========== 供 Tick / Tool 调用 ==========

        /// <summary>取走所有待推送通知。</summary>
        public static List<Notification> Drain()
        {
            var list = new List<Notification>();
            while (Pending.TryDequeue(out var n))
                list.Add(n);
            if (list.Count > 0)
                McpLog.Info($"[notify] drain {list.Count}件, 队列剩余 {Pending.Count}");
            return list;
        }

        /// <summary>获取当前活跃警报的只读快照（返回副本，不持游戏引用）。</summary>
        public static IReadOnlyList<AlertInfo> GetActiveAlerts()
        {
            return ActiveAlerts.Values.ToList();
        }

        /// <summary>取走待推送通知并格式化。</summary>
        public static string DrainFormatted()
        {
            var notifications = Drain();
            if (notifications.Count == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var n in notifications)
            {
                switch (n.Type)
                {
                    case NotificationType.Letter:
                        sb.AppendLine($"[{n.DangerLabel}] {n.Label}");
                        if (!string.IsNullOrEmpty(n.Text))
                            sb.AppendLine(n.Text);
                        break;
                    case NotificationType.Message:
                        sb.AppendLine($"[{n.DangerLabel}] {n.Text}");
                        break;
                    case NotificationType.AlertStart:
                        sb.Append($"! [{n.PriorityLabel}] {n.Label}");
                        if (n.Culprits != null && n.Culprits.Count > 0)
                            sb.Append($": {string.Join(", ", n.Culprits.Take(5))}");
                        sb.AppendLine();
                        break;
                    case NotificationType.AlertEnd:
                        sb.AppendLine($"   [{n.Label} 已解除]");
                        break;
                }
            }
            return sb.ToString().TrimEnd();
        }

        private static string MapCategory(NotificationType type, string dangerLabel)
        {
            return dangerLabel switch
            {
                "大威胁" or "小威胁" or "Boss" => "Combat",
                "死亡" or "角色死亡" => "Combat",
                "健康事件" => "Health",
                "食物" => "Food",
                "被困" or "被困-紧急" => "Survival",
                "正面" or "事件" or "来人" or "成长" => "Economy",
                "研究" => "Research",
                "建造" or "制作" or "蓝图" => "Construction",
                "仪式失败" or "仪式成功" => "Mood",
                _ => "Economy"
            };
        }

        /// <summary>清空所有状态（新游戏开始时调用）。</summary>
        public static void Reset()
        {
            var pendingCount = Pending.Count;
            var alertCount = ActiveAlerts.Count;
            var letterCount = NotifiedLetters.Count;
            var msgCount = NotifiedMessages.Count;
            NotifiedLetters.Clear();
            NotifiedMessages.Clear();
            ActiveAlerts.Clear();
            HighDangerPending = false;
            while (Pending.TryDequeue(out _)) { }
            McpLog.Info($"[notify] reset — 清空 pending={pendingCount} alerts={alertCount} letters={letterCount} msgs={msgCount}");
        }
    }
}
