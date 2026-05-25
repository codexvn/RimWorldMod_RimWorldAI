using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimWorldMCP
{
    public enum MessageCategory
    {
        DailyMorning = 0,
        Alert = 10,
        DialogPrompt = 25,
        RaidEnd = 20,
        RaidStart = 30,
        SessionInit = 40,
    }

    internal struct PendingMessage
    {
        public MessageCategory Category;
        public string Text;
    }

    /// <summary>消息队列 — 同类覆盖 + 等 agent stream 完成才发下一条</summary>
    public static class GatewayMessageQueue
    {
        private static readonly Dictionary<MessageCategory, PendingMessage> _pending = new();
        private static bool _sending;
        private static MessageCategory _currentSendingCategory;
        private static bool _abortSent;
        private static int _idleFrames;
        private const int IdleFramesBeforeSend = 30; // ~0.5s 窗口让同类消息覆盖
        private static int _postAbortUntilMs; // Environment.TickCount 时间戳，游戏加速不影响
        private static int _lastSendTick;
        private static int _lastDailyDaySent = -1;
        private static bool _sessionPromptSent;

        /// <summary>最后一次成功发送 agent 消息时的游戏 tick</summary>
        public static int LastSendTick => _lastSendTick;

        public static void Enqueue(MessageCategory category, string text)
        {
            if (!GatewayClient.IsConnected) return;

            _pending[category] = new PendingMessage { Category = category, Text = text };
            _idleFrames = IdleFramesBeforeSend;
        }

        /// <summary>每帧调用</summary>
        public static void Tick()
        {
            if (!GatewayClient.IsConnected)
            {
                _sending = false;
                _abortSent = false;
                return;
            }

            if (!GatewayClient.IsReady) return;

            // 正在发送中——有通知即打断
            if (_sending)
            {
                if (_pending.Count > 0 && !_abortSent)
                {
                    GatewayClient.AbortAgent();
                    _abortSent = true;
                }
                return;
            }

            if (_pending.Count == 0) return;

            // 打断后冷却期（真实时间 500ms），防止立刻重发导致并发问题
            if (_postAbortUntilMs > 0 && Environment.TickCount < _postAbortUntilMs)
                return;

            // 短暂稳定窗口让同类消息覆盖（但高危消息立即发）
            if (_idleFrames > 0)
            {
                var highest = _pending.Values.OrderByDescending(m => (int)m.Category).First();
                if ((int)highest.Category < (int)MessageCategory.DialogPrompt)
                {
                    _idleFrames--;
                    return;
                }
                _idleFrames = 0;
            }

            // 取最高优先级发送
            var bestMsg = _pending.Values.OrderByDescending(m => (int)m.Category).First();
            _pending.Remove(bestMsg.Category);
            _ = DoSend(bestMsg.Category, bestMsg.Text);
        }

        public static void SendNow(MessageCategory category, string text)
        {
            if (!GatewayClient.IsReady) return;

            if (!_sending)
            {
                _ = DoSend(category, text);
                return;
            }

            // 正在发送中——有通知即打断
            if (!_abortSent)
            {
                GatewayClient.AbortAgent();
                _abortSent = true;
            }
            _pending[category] = new PendingMessage { Category = category, Text = text };
        }

        public static void MarkDailySent(int day) => _lastDailyDaySent = day;
        public static bool WasDailySentToday(int day) => _lastDailyDaySent == day;
        public static void MarkSessionPromptSent() => _sessionPromptSent = true;
        public static bool WasSessionPromptSent => _sessionPromptSent;

        public static void Reset()
        {
            _pending.Clear();
            _sending = false;
            _abortSent = false;
            _postAbortUntilMs = 0;
            _lastSendTick = 0;
            _lastDailyDaySent = -1;
            _sessionPromptSent = false;
        }

        private static async System.Threading.Tasks.Task DoSend(MessageCategory category, string text)
        {
            if (!GatewayClient.IsReady) return;
            _sending = true;
            _currentSendingCategory = category;
            try
            {
                await GatewayClient.SendMessage(text);
                _lastSendTick = Find.TickManager?.TicksGame ?? 0;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[queue] 发送失败 ({category}): {ex.Message}");
            }
            finally
            {
                _sending = false;
                if (_abortSent)
                    _postAbortUntilMs = Environment.TickCount + 500;
                _abortSent = false;
            }
        }
    }
}
