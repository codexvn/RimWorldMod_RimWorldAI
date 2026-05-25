using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    /// <summary>右上角 AI 对话窗口，半透明、长方形，实时显示最新流式回复</summary>
    public class Dialog_AiChat : Window
    {
        private Vector2 _scrollPos;
        private bool _scrollToBottom;
        private string _inputText = "";
        private string _pendingSendText = "";
        private static float _alpha = 0.8f;
        private static readonly Color UserBgColor = new Color(0.12f, 0.18f, 0.30f, 1f);
        private static readonly Color AiBgColor = new Color(0.08f, 0.22f, 0.10f, 1f);
        private static readonly Color ErrorBgColor = new Color(0.30f, 0.08f, 0.08f, 1f);

        public Dialog_AiChat()
        {
            optionalTitle = "AI 对话";
            doCloseX = true;
            closeOnCancel = true;
            closeOnAccept = false;
            closeOnClickedOutside = false;
            draggable = true;
            resizeable = true;
            forcePause = false;
            layer = WindowLayer.Dialog;
            preventCameraMotion = false;
            doWindowBackground = false;
            drawShadow = false;
        }

        public override Vector2 InitialSize =>
            new Vector2(UI.screenWidth / 3f, UI.screenHeight / 3f);

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(UI.screenWidth - InitialSize.x - 10f, 10f,
                InitialSize.x, InitialSize.y);
            windowRect = windowRect.Rounded();
        }

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

        /// <summary>发送输入框文本（SendMessage 入口统一 abort，此处只缓存文本）</summary>
        private void TrySendInput()
        {
            var text = _inputText.Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (!CCClient.IsConnected) return;
            if (!string.IsNullOrEmpty(_pendingSendText)) return;

            _inputText = "";
            _pendingSendText = text;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 全局 Enter 键发送（在 GUI 绘制前处理，参考 Dialog_GiveName.cs）
            if (Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                TrySendInput();
                Event.current.Use();
            }

            // 处理延迟发送（SendMessage 入口统一 abort）
            if (!string.IsNullOrEmpty(_pendingSendText))
            {
                var t = _pendingSendText;
                _pendingSendText = "";
                if (CCClient.IsReady)
                    _ = CCClient.SendEventText("rimworld.chat", "UserMessage", t);
            }

            // 半透明背景
            Widgets.DrawBoxSolid(inRect, new Color(0.05f, 0.05f, 0.05f, _alpha));

            var entries = ChatDisplayState.Snapshot;
            var toolCalls = ChatDisplayState.ToolCallsSnapshot;

            float toolStripH = toolCalls.Count > 0 ? 22f : 0f; // 工具状态条
            float inputRowH = 28f;
            float footerHeight = 30f;
            float topMargin = 6f;

            Rect scrollRect = new Rect(inRect.x + 6f, inRect.y + topMargin,
                inRect.width - 12f, inRect.height - toolStripH - inputRowH - footerHeight - topMargin - 4f);

            // 内容宽 = 可视区 - 垂直滚动条宽度(16) - 边距，避免横向滚动条
            float contentWidth = scrollRect.width - 16f - 4f;
            float totalH = 4f;
            foreach (var entry in entries)
            {
                CalcEntryHeight(entry, contentWidth);
                totalH += entry.CachedHeight + 8f;
            }

            Rect viewRect = new Rect(0f, 0f, contentWidth,
                Mathf.Max(totalH, scrollRect.height));
            Widgets.BeginScrollView(scrollRect, ref _scrollPos, viewRect);

            float curY = 4f;
            foreach (var entry in entries)
            {
                curY += DrawEntry(entry, viewRect, contentWidth, curY);
                curY += 8f;
            }

            Widgets.EndScrollView();

            if (_scrollToBottom && entries.Count > 0)
            {
                _scrollPos.y = Mathf.Max(0f, viewRect.height - scrollRect.height);
                _scrollToBottom = false;
            }

            // 工具调用状态条（动态刷新）
            float stripY = scrollRect.yMax + 2f;
            DrawToolStrip(inRect, stripY, toolStripH, toolCalls);

            // 输入行
            float inputRowY = stripY + toolStripH + 2f;
            DrawInputRow(inRect, inputRowY, inputRowH);

            // 底部控制栏
            float footerY = inputRowY + inputRowH + 2f;
            DrawFooter(inRect, footerY, footerHeight);
        }

        private void DrawToolStrip(Rect inRect, float y, float h, System.Collections.Generic.List<ToolCallInfo> toolCalls)
        {
            if (toolCalls.Count == 0) return;

            float x = inRect.x + 6f;
            float width = inRect.width - 12f;

            // 半透明背景条
            Widgets.DrawBoxSolid(new Rect(x, y, width, h),
                new Color(0.1f, 0.1f, 0.15f, _alpha));

            Text.Font = GameFont.Tiny;
            float curX = x + width - 4f;
            for (int i = toolCalls.Count - 1; i >= 0; i--)
            {
                var tc = toolCalls[i];
                // 格式: "调用工具: xxx（参数）"
                // Unity GUI.Label 会把 _ 当成键盘快捷键标记吃掉，双写 __ 可显示一个下划线
                string displayName = tc.Name?.Replace("_", "__") ?? "";
                string meta = tc.Meta?.Replace("_", "__") ?? "";
                string label = !string.IsNullOrEmpty(meta)
                    ? $"调用工具: {displayName}（{meta}）"
                    : $"调用工具: {displayName}";
                if (string.IsNullOrEmpty(label)) continue;

                Color c;
                if (tc.Status == ToolStatus.Running)
                    c = new Color(1f, 0.8f, 0.3f, _alpha); // 黄色
                else if (tc.Status == ToolStatus.Failed)
                    c = new Color(1f, 0.3f, 0.3f, _alpha); // 红色
                else
                    c = new Color(0.3f, 1f, 0.3f, _alpha); // 绿色

                float labelWidth = Text.CalcSize(label).x + 8f;
                curX -= labelWidth;
                if (curX < x + 4f) break;

                GUI.color = c;
                Widgets.Label(new Rect(curX, y + 4f, labelWidth, 16f), label);
                GUI.color = Color.white;
                curX -= 4f;
            }
            Text.Font = GameFont.Small;
        }

        private void DrawInputRow(Rect inRect, float y, float h)
        {
            // 用户随时可以发送（发送时自动打断当前 agent 任务），仅断开连接时置灰
            bool canSend = CCClient.IsConnected;

            float btnW = 52f;
            float gap = 4f;
            float x = inRect.x + 6f;
            float width = inRect.width - 12f;

            // 输入框
            Rect tfRect = new Rect(x, y + 2f, width - btnW - gap, h - 4f);
            GUI.color = canSend ? Color.white : Color.grey;
            GUI.SetNextControlName("chatInput");
            _inputText = Widgets.TextField(tfRect, _inputText);
            GUI.color = Color.white;

            // 发送按钮
            Rect sendRect = new Rect(tfRect.xMax + gap, y + 2f, btnW, h - 4f);
            GUI.color = canSend ? Color.white : Color.grey;
            if (Widgets.ButtonText(sendRect, "发送"))
            {
                if (canSend)
                    TrySendInput();
            }
            GUI.color = Color.white;
        }

        private void DrawFooter(Rect inRect, float y, float h)
        {
            float alphaBtnW = 24f;
            float abortBtnW = 56f;

            // 透明度 -
            Rect alphaMinus = new Rect(inRect.x + 4f, y + 4f, alphaBtnW, h - 8f);
            if (Widgets.ButtonText(alphaMinus, "-"))
                _alpha = Mathf.Clamp(_alpha - 0.1f, 0.2f, 1f);
            TooltipHandler.TipRegion(alphaMinus, $"透明度 {(int)(_alpha * 100)}%");

            // 透明度 +
            Rect alphaPlus = new Rect(inRect.x + 30f, y + 4f, alphaBtnW, h - 8f);
            if (Widgets.ButtonText(alphaPlus, "+"))
                _alpha = Mathf.Clamp(_alpha + 0.1f, 0.2f, 1f);
            TooltipHandler.TipRegion(alphaPlus, $"透明度 {(int)(_alpha * 100)}%");

            bool connected = CCClient.IsConnected;

            // 中断按钮
            float abortX = inRect.width - abortBtnW - 4f;
            Rect abortRect = new Rect(abortX, y + 4f, abortBtnW, h - 8f);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(abortRect, "中断"))
            {
                if (CCClient.IsReady)
                    _ = CCClient.SendEventText("rimworld.chat", "Abort", "");
            }

            // 清空
            Rect clearRect = new Rect(abortX - 58f, y + 4f, 48f, h - 8f);
            GUI.color = Color.white;
            if (Widgets.ButtonText(clearRect, "清空"))
                ChatDisplayState.Clear();

            // 继续 — 向 agent 发送殖民地概览（SendMessage 入口统一 abort）
            Rect continueRect = new Rect(abortX - 120f, y + 4f, 54f, h - 8f);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(continueRect, "继续"))
            {
                if (connected)
                {
                    var map = Find.CurrentMap;
                    if (map != null)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        _pendingSendText = GameContextProvider.BuildColonyOverview(map, colonists, colonists.Count);
                    }
                }
            }
            GUI.color = Color.white;
        }

        private static void CalcEntryHeight(ChatEntry entry, float contentWidth)
        {
            // 已完成的消息高度不变，流式消息只在文本增长时重算
            var text = entry.Text ?? "";
            if (entry.State == ChatState.Done && entry.CachedHeight > 0f) return;
            if (entry.CachedHeight > 0f && text.Length == entry.CachedTextLen) return;

            float labelWidth = contentWidth - 32f; // 与 DrawEntry bodyRect 宽度一致
            float bodyHeight = Text.CalcHeight(text.StripTags(), labelWidth);
            entry.CachedHeight = 18f + Mathf.Max(bodyHeight, 10f);
            entry.CachedTextLen = text.Length;
        }

        private static float DrawEntry(ChatEntry entry, Rect viewRect, float contentWidth, float y)
        {
            string label = entry.Role == ChatRole.User ? "你" : "AI";
            // Unity GUI.Label 把 _ 当作键盘快捷键标记吃掉，双写 __ 可显示一个下划线
            string body = (entry.Text ?? "").Replace("_", "__");
            if (entry.State == ChatState.Streaming)
            {
                bool showCursor = Time.realtimeSinceStartup % 1.0f < 0.6f;
                body += showCursor ? "▌" : " ";
            }

            float bodyWidth = contentWidth - 20f;
            float bodyHeight = Mathf.Max(0f, entry.CachedHeight - 18f);
            float entryHeight = entry.CachedHeight;

            Rect bubbleRect = new Rect(2f, y, contentWidth, entryHeight);
            Color bgColor = entry.Role == ChatRole.User ? UserBgColor
                : entry.State == ChatState.Error ? ErrorBgColor : AiBgColor;
            bgColor.a = _alpha;
            Widgets.DrawBoxSolid(bubbleRect, bgColor);

            // 右键复制，不干扰滚动拖拽
            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 1
                && Mouse.IsOver(bubbleRect))
            {
                GUIUtility.systemCopyBuffer = entry.Text;
                Messages.Message("已复制到剪贴板", MessageTypeDefOf.SilentInput, false);
                Event.current.Use();
            }

            // 标签
            Rect labelRect = new Rect(bubbleRect.x + 6f, bubbleRect.y + 2f, 24f, 16f);
            Text.Font = GameFont.Tiny;
            GUI.color = entry.Role == ChatRole.User
                ? new Color(0.4f, 0.8f, 1f, _alpha)
                : new Color(0.4f, 1f, 0.4f, _alpha);
            Widgets.Label(labelRect, label);

            // 消息正文
            Rect bodyRect = new Rect(bubbleRect.x + 8f, bubbleRect.y + 17f,
                bodyWidth - 12f, Mathf.Max(bodyHeight, 10f));
            GUI.color = new Color(1f, 1f, 1f, _alpha);
            Text.Font = GameFont.Small;
            Widgets.Label(bodyRect, body);

            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            return entryHeight;
        }
    }
}
