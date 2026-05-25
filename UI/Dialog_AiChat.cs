using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    /// <summary>游戏内 AI 对话窗口，实时显示流式回复</summary>
    public class Dialog_AiChat : Window
    {
        private Vector2 _scrollPos;
        private bool _scrollToBottom;
        private static readonly Color UserBgColor = new Color(0.15f, 0.22f, 0.35f);
        private static readonly Color AiBgColor = new Color(0.1f, 0.28f, 0.12f);
        private static readonly Color ErrorBgColor = new Color(0.35f, 0.1f, 0.1f);

        public Dialog_AiChat()
        {
            optionalTitle = "AI 对话";
            doCloseX = true;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            forcePause = false;
            layer = WindowLayer.Dialog;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize => new Vector2(550f, 480f);

        public override void PreOpen()
        {
            base.PreOpen();
            ChatDisplayState.OnChanged += OnChatChanged;
        }

        public override void PostClose()
        {
            ChatDisplayState.OnChanged -= OnChatChanged;
            base.PostClose();
        }

        private void OnChatChanged()
        {
            _scrollToBottom = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var entries = ChatDisplayState.Snapshot;

            Rect scrollRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 38f);
            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, 0f);
            // 先计算总高度
            float totalHeight = 0f;
            foreach (var entry in entries)
            {
                totalHeight += CalcEntryHeight(entry, inRect.width - 24f) + 8f;
            }
            viewRect.height = Mathf.Max(totalHeight + 4f, scrollRect.height);
            Widgets.BeginScrollView(scrollRect, ref _scrollPos, viewRect);

            float curY = 4f;
            float contentWidth = inRect.width - 24f;
            foreach (var entry in entries)
            {
                curY += DrawEntry(entry, viewRect, contentWidth, curY);
                curY += 8f; // gap
            }

            Widgets.EndScrollView();

            if (_scrollToBottom && entries.Count > 0)
            {
                _scrollPos.y = Mathf.Max(0f, viewRect.height - scrollRect.height);
                _scrollToBottom = false;
            }

            // 底部中断按钮
            bool connected = GatewayClient.IsConnected;
            float btnWidth = 80f;
            float btnHeight = 30f;
            Rect abortRect = new Rect(inRect.width - btnWidth - 4f, inRect.height - btnHeight - 4f, btnWidth, btnHeight);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(abortRect, "中断 AI"))
            {
                if (GatewayClient.IsReady)
                    GatewayClient.AbortAgent();
            }
            GUI.color = Color.white;
        }

        private float CalcEntryHeight(ChatEntry entry, float contentWidth)
        {
            string label = entry.Role == ChatRole.User ? "你:" : "AI:";
            var text = entry.Text.StripTags();
            // 消息体区域 = 总宽 - 边距 - 标签缩进
            float bodyWidth = contentWidth - 24f;
            float bodyHeight = Text.CalcHeight(text, bodyWidth);
            // 标签行高度 + 消息文本
            return 20f + Mathf.Max(bodyHeight, 12f);
        }

        private float DrawEntry(ChatEntry entry, Rect viewRect, float contentWidth, float y)
        {
            string label = entry.Role == ChatRole.User ? "你:" : "AI:";
            string body = entry.Text ?? "";
            if (entry.State == ChatState.Streaming)
            {
                bool showCursor = Time.realtimeSinceStartup % 1.0f < 0.6f;
                body += showCursor ? "▌" : " ";
            }

            // 计算高度
            float bodyWidth = contentWidth - 24f;
            float bodyHeight = Text.CalcHeight(body, bodyWidth);
            float entryHeight = 20f + Mathf.Max(bodyHeight, 12f);

            Rect fullRect = new Rect(4f, y, contentWidth, entryHeight);
            Color bgColor = entry.Role == ChatRole.User ? UserBgColor
                : entry.State == ChatState.Error ? ErrorBgColor : AiBgColor;

            // 背景
            Widgets.DrawBoxSolid(fullRect, bgColor);

            Rect labelRect = new Rect(fullRect.x + 6f, fullRect.y + 2f, 36f, 18f);
            Text.Font = GameFont.Tiny;
            GUI.color = entry.Role == ChatRole.User ? Color.cyan : Color.green;
            Widgets.Label(labelRect, label);
            GUI.color = Color.white;

            Rect bodyRect = new Rect(fullRect.x + 8f, fullRect.y + 20f, bodyWidth - 16f, Mathf.Max(bodyHeight, 12f));
            Text.Font = GameFont.Small;
            Widgets.Label(bodyRect, body);
            return entryHeight;
        }
    }
}
