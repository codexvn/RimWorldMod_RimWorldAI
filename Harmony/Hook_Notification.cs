using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldMCP.Harmony
{
    [StaticConstructorOnStartup]
    public static class Hook_Notification
    {
        static Hook_Notification()
        {
            var harmony = new HarmonyLib.Harmony("com.rimworldmcp.notification");
            harmony.PatchAll(typeof(Hook_Notification).Assembly);
            McpLog.Info("Harmony notification patches installed.");
        }

        // ========== Letter 拦截 ==========

        [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
            typeof(Letter), typeof(string), typeof(int), typeof(bool))]
        public static class Patch_LetterStack_ReceiveLetter
        {
            static void Prefix(Letter let)
            {
                if (let == null) return;
                if (NotificationBus.IsLetterNotified(let.ID)) return;
                NotificationBus.MarkLetterNotified(let.ID);

                string label = let.Label.Resolve();
                string? text = null;
                if (let is ChoiceLetter cl)
                {
                    text = cl.Text.Resolve();
                    if (text.Length > 500) text = text.Substring(0, 497) + "...";
                }

                NotificationBus.Enqueue(new Notification
                {
                    Type = NotificationType.Letter,
                    Label = label,
                    Text = text,
                    DangerLabel = ClassifyLetter(let.def),
                    Tick = Find.TickManager?.TicksGame ?? 0
                });
            }
        }

        // ========== Message 拦截 ==========

        [HarmonyPatch(typeof(Messages), nameof(Messages.Message),
            typeof(Message), typeof(bool))]
        public static class Patch_Messages_Message
        {
            static void Prefix(Message msg)
            {
                if (msg == null || string.IsNullOrEmpty(msg.text)) return;
                string id = ((ILoadReferenceable)msg).GetUniqueLoadID();
                if (NotificationBus.IsMessageNotified(id)) return;
                NotificationBus.MarkMessageNotified(id);

                string text = msg.text;
                if (text.Length > 300) text = text.Substring(0, 297) + "...";

                NotificationBus.Enqueue(new Notification
                {
                    Type = NotificationType.Message,
                    Text = text,
                    DangerLabel = ClassifyMessage(msg.def),
                    Tick = Find.TickManager?.TicksGame ?? 0
                });
            }
        }

        // ========== Alert 拦截 ==========

        [HarmonyPatch(typeof(AlertsReadout), "CheckAddOrRemoveAlert",
            typeof(Alert), typeof(bool))]
        public static class Patch_CheckAddOrRemoveAlert
        {
            static void Prefix(Alert alert, out bool __state)
            {
                __state = alert.Active;
            }

            static void Postfix(Alert alert, bool __state)
            {
                bool wasActive = __state;
                bool isActive = alert.Active;
                if (wasActive == isActive) return;

                string key = alert.GetType().Name;

                // 拷贝数据，不持 Alert 引用
                var culprits = GetCulpritNames(alert);
                var culpritsArr = culprits?.ToArray() ?? System.Array.Empty<string?>();

                if (isActive)
                {
                    string label = alert.Label;
                    NotificationBus.OnAlertStarted(key, label, (int)alert.Priority, culpritsArr);
                }
                else
                {
                    // 从自有镜像取标签（alert.Label 此时为空）
                    string? label = NotificationBus.GetAlertLabel(key);
                    if (string.IsNullOrEmpty(label)) label = key;
                    NotificationBus.OnAlertEnded(key);

                    // AlertEnd 也入队推送
                    NotificationBus.Enqueue(new Notification
                    {
                        Type = NotificationType.AlertEnd,
                        Label = label!,
                        Tick = Find.TickManager?.TicksGame ?? 0
                    });
                    return;
                }

                // AlertStart 入队推送
                NotificationBus.Enqueue(new Notification
                {
                    Type = NotificationType.AlertStart,
                    Label = alert.Label,
                    Priority = (int)alert.Priority,
                    Culprits = culprits,
                    Tick = Find.TickManager?.TicksGame ?? 0
                });
            }

            private static List<string>? GetCulpritNames(Alert alert)
            {
                try
                {
                    var report = alert.GetReport();
                    var pawns = report.culpritsPawns;
                    if (pawns != null && pawns.Count > 0)
                        return pawns.Take(5).Select(p => p.Name.ToStringShort).ToList();
                }
                catch { }
                return null;
            }
        }

        // ========== 游戏减速拦截 ==========

        [HarmonyPatch(typeof(TimeSlower), nameof(TimeSlower.SignalForceNormalSpeed))]
        public static class Patch_SignalForceNormalSpeed
        {
            static void Postfix()
            {
                NotificationBus.NotifySpeedSlowdown("游戏速度强制降至1x (800 ticks)");
            }
        }

        [HarmonyPatch(typeof(TimeSlower), nameof(TimeSlower.SignalForceNormalSpeedShort))]
        public static class Patch_SignalForceNormalSpeedShort
        {
            static void Postfix()
            {
                NotificationBus.NotifySpeedSlowdown("游戏速度强制降至1x (240 ticks)");
            }
        }

        // ========== 游戏生命周期：退出/返回主菜单时清理 Companion 进程 ==========

        [HarmonyPatch(typeof(Game), nameof(Game.Dispose))]
        public static class Patch_Game_Dispose
        {
            static void Postfix()
            {
                BridgeLifecycle.Stop();
            }
        }

        // ========== 分类辅助 ==========

        private static string ClassifyLetter(LetterDef def)
        {
            if (def == LetterDefOf.ThreatBig) return "大威胁";
            if (def == LetterDefOf.ThreatSmall) return "小威胁";
            if (def == LetterDefOf.NegativeEvent) return "负面";
            if (def == LetterDefOf.PositiveEvent) return "正面";
            if (def == LetterDefOf.Death) return "死亡";
            if (def == LetterDefOf.NeutralEvent) return "事件";
            if (def == LetterDefOf.AcceptVisitors || def == LetterDefOf.AcceptJoiner) return "来人";
            if (def == LetterDefOf.BabyBirth || def == LetterDefOf.BabyToChild
                || def == LetterDefOf.ChildToAdult || def == LetterDefOf.ChildBirthday) return "成长";
            if (def == LetterDefOf.RelicHuntInstallationFound || def == LetterDefOf.EntityDiscovered) return "任务";
            if (def == LetterDefOf.RitualOutcomePositive) return "仪式成功";
            if (def == LetterDefOf.RitualOutcomeNegative) return "仪式失败";
            if (def == LetterDefOf.ChoosePawn) return "选择角色";
            if (def == LetterDefOf.GameEnded) return "游戏结束";
            if (def == LetterDefOf.Bossgroup) return "Boss";
            if (def == LetterDefOf.BundleLetter) return "捆绑";
            if (def == LetterDefOf.AcceptCreepJoiner) return "来人";
            return "通知";
        }

        private static string ClassifyMessage(MessageTypeDef def)
        {
            if (def == MessageTypeDefOf.ThreatBig) return "大威胁";
            if (def == MessageTypeDefOf.ThreatSmall) return "小威胁";
            if (def == MessageTypeDefOf.PawnDeath) return "角色死亡";
            if (def == MessageTypeDefOf.NegativeHealthEvent) return "健康事件";
            if (def == MessageTypeDefOf.NegativeEvent) return "负面";
            if (def == MessageTypeDefOf.NeutralEvent) return "事件";
            if (def == MessageTypeDefOf.PositiveEvent) return "正面";
            if (def == MessageTypeDefOf.TaskCompletion) return "完成";
            if (def == MessageTypeDefOf.SituationResolved) return "状态解除";
            if (def == MessageTypeDefOf.RejectInput) return "拒绝";
            if (def == MessageTypeDefOf.CautionInput) return "警告";
            if (def == MessageTypeDefOf.SilentInput) return "静默";
            return def?.defName ?? "消息";
        }
    }
}
