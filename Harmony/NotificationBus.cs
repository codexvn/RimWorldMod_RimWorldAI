using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldMCP.Harmony
{
    /// <summary>自有警报数据（拷贝，不持游戏对象引用）</summary>
    public class AlertInfo
    {
        public string Key { get; set; } = string.Empty;       // 类型名，如 "Alert_NeedDoctor"
        public string Label { get; set; } = string.Empty;
        public int Priority { get; set; }
        public string?[] Culprits { get; set; } = System.Array.Empty<string?>();
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

        // ========== 供 Patch 调用 ==========

        public static void Enqueue(Notification n)
        {
            Pending.Enqueue(n);
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
            ActiveAlerts[key] = new AlertInfo
            {
                Key = key,
                Label = label,
                Priority = priority,
                Culprits = culprits
            };
        }

        /// <summary>警报解除：从镜像移除。</summary>
        public static void OnAlertEnded(string key)
        {
            ActiveAlerts.Remove(key);
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

        /// <summary>清空所有状态（新游戏开始时调用）。</summary>
        public static void Reset()
        {
            NotifiedLetters.Clear();
            NotifiedMessages.Clear();
            ActiveAlerts.Clear();
            while (Pending.TryDequeue(out _)) { }
        }
    }
}
