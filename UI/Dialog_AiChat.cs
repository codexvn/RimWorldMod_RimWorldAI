using System;
using RimWorld;
using RimWorldMCP.Helpers;
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
        private static float _alpha = 0.8f;
        private const float ToolScrollSpeed = 35f;
        private static readonly Color UserBgColor = new Color(0.12f, 0.18f, 0.30f, 1f);
        private static readonly Color AiBgColor = new Color(0.08f, 0.22f, 0.10f, 1f);
        private static readonly Color SubagentBgColor = new Color(0.15f, 0.08f, 0.22f, 1f);
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
            doWindowBackground = true;
            drawShadow = true;
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

        private void TrySendInput()
        {
            var text = _inputText.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (!CCClient.IsReady)
            {
                Messages.Message("Claude Code 未连接，无法发送消息", MessageTypeDefOf.RejectInput, false);
                return;
            }

            _inputText = "";
            _ = CCClient.SendAbort();
            ChatDisplayState.OnUserMessage(text);
            _ = CCClient.SendEventText("rimworld.chat", "UserMessage", text);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                TrySendInput();
                Event.current.Use();
            }

            var entries = ChatDisplayState.Snapshot;
            var toolCalls = ChatDisplayState.ToolCallsSnapshot;

            float toolStripH = CalcToolStripHeight(toolCalls, inRect.width - 12f);
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

            // 工具条出现后修正滚动位置，防止最后消息被遮挡
            if (_scrollPos.y + scrollRect.height > viewRect.height)
                _scrollPos.y = Mathf.Max(0f, viewRect.height - scrollRect.height);

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

        private static float CalcToolStripHeight(System.Collections.Generic.List<ToolCallInfo> toolCalls, float stripWidth)
        {
            foreach (var tc in toolCalls)
                if (tc.Status == ToolStatus.Running)
                    return 20f;
            return 0f;
        }

        private void DrawToolStrip(Rect inRect, float y, float h, System.Collections.Generic.List<ToolCallInfo> toolCalls)
        {
            var running = new System.Collections.Generic.List<ToolCallInfo>();
            foreach (var tc in toolCalls)
                if (tc.Status == ToolStatus.Running)
                    running.Add(tc);
            if (running.Count == 0) return;

            float x = inRect.x + 6f;
            float width = inRect.width - 12f;

            Widgets.DrawBoxSolid(new Rect(x, y, width, h),
                new Color(0.1f, 0.1f, 0.15f, _alpha));

            Text.Font = GameFont.Tiny;
            float sepW = Text.CalcSize("  ◆  ").x;

            // 构建轮播段落
            var segments = new System.Collections.Generic.List<string>();
            float totalW = 0f;
            for (int i = 0; i < running.Count; i++)
            {
                if (i > 0) totalW += sepW;
                string name = ToolDisplayNames.GetDisplayName(running[i].Name ?? "").Replace("_", "__");
                segments.Add(name);
                totalW += Text.CalcSize(name).x;
            }
            if (totalW == 0f) { Text.Font = GameFont.Small; return; }

            // 不需要滚动时居中
            if (totalW <= width)
            {
                float drawX = x + (width - totalW) / 2f;
                for (int i = 0; i < segments.Count; i++)
                {
                    if (i > 0)
                    {
                        GUI.color = new Color(0.5f, 0.5f, 0.5f, _alpha);
                        Widgets.Label(new Rect(drawX, y + 2f, sepW, 16f), "◆");
                        drawX += sepW;
                    }
                    GUI.color = new Color(1f, 0.75f, 0.2f, _alpha);
                    float sw = Text.CalcSize(segments[i]).x;
                    Widgets.Label(new Rect(drawX, y + 2f, sw, 16f), segments[i]);
                    drawX += sw;
                }
            }
            else
            {
                // 轮播：利用截断区循环绘制，实现无缝滚动
                float offset = (Time.realtimeSinceStartup * ToolScrollSpeed) % totalW;
                GUI.BeginGroup(new Rect(x, y, width, h));

                float px = -offset;
                int copies = 0;
                while (px < width && copies < 10)
                {
                    for (int i = 0; i < segments.Count && px < width; i++)
                    {
                        if (copies > 0 || i > 0)
                        {
                            GUI.color = new Color(0.5f, 0.5f, 0.5f, _alpha);
                            Widgets.Label(new Rect(px, 2f, sepW, 16f), "◆");
                            px += sepW;
                        }
                        float sw = Text.CalcSize(segments[i]).x;
                        if (px + sw > 0)
                        {
                            GUI.color = new Color(1f, 0.75f, 0.2f, _alpha);
                            Widgets.Label(new Rect(px, 2f, sw, 16f), segments[i]);
                        }
                        px += sw;
                    }
                    copies++;
                }

                GUI.EndGroup();
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private void DrawInputRow(Rect inRect, float y, float h)
        {
            bool canSend = true;

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
            bool connected = CCClient.IsReady;
            float alphaBtnW = 24f;

            // 连接状态指示
            float statusX = inRect.x + 4f;
            Rect statusRect = new Rect(statusX, y + 4f, 80f, h - 8f);
            Text.Font = GameFont.Tiny;
            GUI.color = connected ? new Color(0.3f, 1f, 0.3f, _alpha) : new Color(1f, 0.4f, 0.4f, _alpha);
            Widgets.Label(statusRect, connected ? "已连接" : "未连接");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 透明度 -
            float alphaX = statusX + 78f;
            Rect alphaMinus = new Rect(alphaX, y + 4f, alphaBtnW, h - 8f);
            if (Widgets.ButtonText(alphaMinus, "-"))
                _alpha = Mathf.Clamp(_alpha - 0.1f, 0.2f, 1f);
            TooltipHandler.TipRegion(alphaMinus, $"透明度 {(int)(_alpha * 100)}%");

            // 透明度 +
            Rect alphaPlus = new Rect(alphaX + alphaBtnW + 2f, y + 4f, alphaBtnW, h - 8f);
            if (Widgets.ButtonText(alphaPlus, "+"))
                _alpha = Mathf.Clamp(_alpha + 0.1f, 0.2f, 1f);
            TooltipHandler.TipRegion(alphaPlus, $"透明度 {(int)(_alpha * 100)}%");

            // 按钮从右往左排列：中断 | 继续 | 清空
            float rightSide = inRect.x + inRect.width - 4f;
            float btnY = y + 4f;
            float btnH = h - 8f;

            float abortBtnW = 56f;
            Rect abortRect = new Rect(rightSide - abortBtnW, btnY, abortBtnW, btnH);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(abortRect, "中断"))
            {
                ChatDisplayState.MarkLastAborted();
                _ = CCClient.SendAbort();
            }
            GUI.color = Color.white;

            float continueBtnW = 54f;
            Rect continueRect = new Rect(abortRect.x - continueBtnW - 4f, btnY, continueBtnW, btnH);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(continueRect, "继续"))
            {
                if (connected)
                {
                    _ = CCClient.SendAbort();
                    var map = Find.CurrentMap;
                    if (map != null)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var overview = GameContextProvider.BuildColonyOverview(map, colonists, colonists.Count);
                        ChatDisplayState.AddSystemMessage(overview);
                        _ = CCClient.SendEventText("rimworld.chat", "ColonyOverview", overview);
                    }
                }
            }
            GUI.color = Color.white;

            float clearBtnW = 48f;
            Rect clearRect = new Rect(continueRect.x - clearBtnW - 4f, btnY, clearBtnW, btnH);
            GUI.color = Color.white;
            if (Widgets.ButtonText(clearRect, "清空"))
                ChatDisplayState.Clear();
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
            entry.CachedHeight = 25f + Mathf.Max(bodyHeight, 10f); // 3(标签上边距)+20(标签高)+2(间距)=25
            entry.CachedTextLen = text.Length;
        }

        private static float DrawEntry(ChatEntry entry, Rect viewRect, float contentWidth, float y)
        {
            bool isSubagent = !string.IsNullOrEmpty(entry.AgentId);
            string label = entry.IsContext ? "系统"
                : entry.Role == ChatRole.User ? "你"
                : isSubagent ? entry.AgentType : "AI";
            // Unity GUI.Label 把 _ 当作键盘快捷键标记吃掉，双写 __ 可显示一个下划线
            string body = (entry.Text ?? "").Replace("_", "__");
            if (entry.State == ChatState.Streaming)
            {
                bool showCursor = Time.realtimeSinceStartup % 1.0f < 0.6f;
                body += showCursor ? "▌" : " ";
            }

            float bodyWidth = contentWidth - 20f;
            float bodyHeight = Mathf.Max(0f, entry.CachedHeight - 25f);
            float entryHeight = entry.CachedHeight;

            Rect bubbleRect = new Rect(2f, y, contentWidth, entryHeight);
            Color bgColor = entry.IsContext ? new Color(0.08f, 0.08f, 0.18f, 1f)
                : entry.Role == ChatRole.User ? UserBgColor
                : entry.State == ChatState.Error ? ErrorBgColor
                : isSubagent ? SubagentBgColor : AiBgColor;
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
            Rect labelRect = new Rect(bubbleRect.x + 6f, bubbleRect.y + 3f,
                isSubagent ? 60f : 24f, 20f);
            Text.Font = GameFont.Small;
            GUI.color = entry.IsContext ? new Color(0.6f, 0.6f, 0.8f, _alpha)
                : entry.Role == ChatRole.User
                    ? new Color(0.4f, 0.8f, 1f, _alpha)
                    : isSubagent
                        ? new Color(0.8f, 0.4f, 1f, _alpha)
                        : new Color(0.4f, 1f, 0.4f, _alpha);
            Widgets.Label(labelRect, label);

            // 消息正文
            Rect bodyRect = new Rect(bubbleRect.x + 8f, labelRect.yMax + 2f,
                bodyWidth - 12f, Mathf.Max(bodyHeight, 10f));
            GUI.color = entry.IsContext ? new Color(0.7f, 0.7f, 0.8f, _alpha)
                : new Color(1f, 1f, 1f, _alpha);
            Text.Font = GameFont.Small;
            Widgets.Label(bodyRect, body);

            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            return entryHeight;
        }
    }
}
