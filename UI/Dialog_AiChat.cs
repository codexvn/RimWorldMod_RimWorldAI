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
        private int _pendingSendUntilMs;
        private static float _alpha = 0.8f;
        private static readonly Color UserBgColor = new Color(0.12f, 0.18f, 0.30f, 1f);
        private static readonly Color AiBgColor = new Color(0.08f, 0.22f, 0.10f, 1f);
        private static readonly Color ErrorBgColor = new Color(0.30f, 0.08f, 0.08f, 1f);

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

        /// <summary>检查 agent 是否忙碌（正在流式输出或执行工具调用）</summary>
        private static bool IsAgentBusy(System.Collections.Generic.List<ChatEntry> entries,
            System.Collections.Generic.List<ToolCallInfo> toolCalls)
        {
            if (entries.Count > 0 && entries[entries.Count - 1].State == ChatState.Streaming)
                return true;
            foreach (var tc in toolCalls)
                if (tc.Status == ToolStatus.Running)
                    return true;
            return false;
        }

        /// <summary>发送输入框文本（先打断，延迟 500ms 后发送）</summary>
        private void TrySendInput()
        {
            var text = _inputText.Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (!GatewayClient.IsConnected) return;
            if (!string.IsNullOrEmpty(_pendingSendText) || _pendingSendUntilMs > 0) return;

            _inputText = "";

            // 先打断当前 agent 任务，参考 GatewayMessageQueue._postAbortUntilMs 冷却模式
            if (GatewayClient.IsReady)
                GatewayClient.AbortAgent();

            ChatDisplayState.OnUserMessage(text);
            _pendingSendText = text;
            _pendingSendUntilMs = Environment.TickCount + 500;
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

            // 处理延迟发送（冷却期满后发送，参考 GatewayMessageQueue._postAbortUntilMs）
            if (!string.IsNullOrEmpty(_pendingSendText) && Environment.TickCount >= _pendingSendUntilMs)
            {
                var t = _pendingSendText;
                _pendingSendText = "";
                _pendingSendUntilMs = 0;
                if (GatewayClient.IsReady)
                    _ = GatewayClient.SendMessage(t);
            }

            // 半透明背景
            Widgets.DrawBoxSolid(inRect, new Color(0.05f, 0.05f, 0.05f, _alpha));

            var entries = ChatDisplayState.Snapshot;
            var toolCalls = ChatDisplayState.ToolCallsSnapshot;
            bool agentBusy = IsAgentBusy(entries, toolCalls);

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
            DrawInputRow(inRect, inputRowY, inputRowH, agentBusy);

            // 底部控制栏 — 复用顶部快照，避免重复 ChatDisplayState.Snapshot 调用
            float footerY = inputRowY + inputRowH + 2f;
            DrawFooter(inRect, footerY, footerHeight, agentBusy);
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
                string label = !string.IsNullOrEmpty(tc.Meta)
                    ? $"调用工具: {tc.Name}（{tc.Meta}）"
                    : $"调用工具: {tc.Name}";
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

        private void DrawInputRow(Rect inRect, float y, float h, bool agentBusy)
        {
            // 用户随时可以发送（发送时自动打断当前 agent 任务），仅断开连接时置灰
            bool canSend = GatewayClient.IsConnected;

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

        private void DrawFooter(Rect inRect, float y, float h, bool agentBusy)
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

            bool connected = GatewayClient.IsConnected;

            // 中断按钮
            float abortX = inRect.width - abortBtnW - 4f;
            Rect abortRect = new Rect(abortX, y + 4f, abortBtnW, h - 8f);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(abortRect, "中断"))
            {
                if (GatewayClient.IsReady)
                    GatewayClient.AbortAgent();
            }

            // 清空
            Rect clearRect = new Rect(abortX - 58f, y + 4f, 48f, h - 8f);
            GUI.color = Color.white;
            if (Widgets.ButtonText(clearRect, "清空"))
                ChatDisplayState.Clear();

            // 继续 — 仅在 agent 空闲时发送殖民地状态
            Rect continueRect = new Rect(abortX - 120f, y + 4f, 54f, h - 8f);
            GUI.color = connected && !agentBusy ? Color.white : Color.grey;
            if (Widgets.ButtonText(continueRect, "继续"))
            {
                if (connected && !agentBusy)
                {
                    var map = Find.CurrentMap;
                    if (map != null)
                    {
                        var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                        var text = GatewayEventMonitor.BuildColonyOverview(map, colonists, colonists.Count);
                        _ = GatewayClient.SendMessage(text);
                    }
                }
            }
            GUI.color = Color.white;
        }

        private static void CalcEntryHeight(ChatEntry entry, float contentWidth)
        {
            var text = (entry.Text ?? "").StripTags();
            float bodyWidth = contentWidth - 20f;
            float bodyHeight = Text.CalcHeight(text, bodyWidth);
            entry.CachedHeight = 18f + Mathf.Max(bodyHeight, 10f);
        }

        private static float DrawEntry(ChatEntry entry, Rect viewRect, float contentWidth, float y)
        {
            string label = entry.Role == ChatRole.User ? "你" : "AI";
            string body = entry.Text ?? "";
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
