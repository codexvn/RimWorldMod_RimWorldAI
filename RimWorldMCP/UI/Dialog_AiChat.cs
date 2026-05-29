using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldMCP.Helpers;
using RimWorldMCP.Tools;
using UnityEngine;
using Verse;

namespace RimWorldMCP
{
    /// <summary>
    /// AI 对话窗口 — 双栏布局：左栏对话流，右栏工具调用记录
    /// </summary>
    public class Dialog_AiChat : Window
    {
        private Vector2 _chatScrollPos;
        private Vector2 _toolScrollPos;
        private Vector2 _todoScrollPos;
        private bool _scrollToBottom;
        private string _inputText = "";
        private static float _alpha = 0.85f;

        private static readonly Color UserBgColor = new Color(0.18f, 0.20f, 0.24f, 1f);
        private static readonly Color AiBgColor = new Color(0.14f, 0.16f, 0.18f, 1f);
        private static readonly Color SubagentBgColor = new Color(0.16f, 0.14f, 0.20f, 1f);
        private static readonly Color ErrorBgColor = new Color(0.24f, 0.12f, 0.12f, 1f);
        private static Color ToolCardBg => new Color(0.12f, 0.14f, 0.17f, _alpha);
        private static Color ToolCardHeaderBg => new Color(0.15f, 0.17f, 0.21f, _alpha);

        protected override float Margin => 6f;

        public Dialog_AiChat()
        {
            optionalTitle = "RimWorld AI Commander";
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
            new Vector2(UI.screenWidth / 3f + 160f, UI.screenHeight / 3f + 80f);

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
            TodoManager.OnChanged += OnTodoChanged;
            _chatUserScrolledUp = false;
            _toolUserScrolledUp = false;
            _scrollToBottom = true;
        }

        public override void PostClose()
        {
            ChatDisplayState.OnChanged -= OnChatChanged;
            TodoManager.OnChanged -= OnTodoChanged;
            base.PostClose();
        }

        private bool _toolScrollToBottom;
        private bool _todoScrollToBottom;
        private int _lastChatCount;
        private int _lastToolCount;
        private bool _chatUserScrolledUp;
        private bool _toolUserScrolledUp;
        private float _lastMaxScroll = -1f;
        private float _lastToolMaxScroll = -1f;

        private void OnChatChanged()
        {
            // 流式期间的滚动由 DrawConversationPanel 直接处理，避免事件驱动滞后闪烁
            var snap = ChatDisplayState.Snapshot;
            bool streaming = snap.Count > 0 && snap[snap.Count - 1].State == ChatState.Streaming;
            // 仅新增条目或流式时滚到底
            if (snap.Count != _lastChatCount) _scrollToBottom = true;
            _lastChatCount = snap.Count;

            var tools = ChatDisplayState.ToolCallsSnapshot;
            if (tools.Count != _lastToolCount) _toolScrollToBottom = true;
            _lastToolCount = tools.Count;
        }
        private void OnTodoChanged() { _todoScrollToBottom = true; }

        private void TrySendInput()
        {
            var text = _inputText.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (!CCClient.IsReady)
            {
                Messages.Message("Claude Code 未连接", MessageTypeDefOf.RejectInput, false);
                return;
            }

            _inputText = "";
            _ = CCClient.SendAbort();
            ChatDisplayState.OnUserMessage(text);
            _ = CCClient.SendEventText("rimworld.chat", "UserMessage", text);
        }

        // ========== 主布局 ==========

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

            float headerH = 22f;
            float inputH = 28f;
            float footerH = 22f;
            float gap = 4f;
            float panelGap = 6f;
            float leftRatio = 0.60f;

            // Header
            DrawHeader(new Rect(inRect.x, inRect.y, inRect.width, headerH));

            // 预算横幅（Warning/Critical/Exceeded 时显示）
            float bannerH = DrawBudgetBanner(new Rect(inRect.x, inRect.y + headerH + gap,
                inRect.width, 22f));
            float bannerOffset = bannerH > 0 ? bannerH + gap : 0;

            // Panels
            float panelsY = inRect.y + headerH + gap + bannerOffset;
            float panelsH = inRect.height - headerH - inputH - footerH - gap * 3;
            float leftW = (inRect.width - panelGap) * leftRatio;
            float rightW = inRect.width - leftW - panelGap;

            // 分隔线（细条）
            float dividerX = inRect.x + leftW + panelGap / 2f;
            Widgets.DrawBoxSolid(new Rect(dividerX, panelsY, 1f, panelsH),
                new Color(0.22f, 0.22f, 0.24f, _alpha));

            // 右栏垂直拆分为二：上半工具调用，下半 TODO
            float rightX = dividerX + panelGap / 2f + 1f;
            float rightContentW = rightW - 2f;
            float rightTopH = panelsH * 0.55f;
            float rightGap = 4f;
            float rightBottomH = panelsH - rightTopH - rightGap;

            DrawConversationPanel(
                new Rect(inRect.x, panelsY, leftW, panelsH), entries);
            DrawToolPanel(
                new Rect(rightX, panelsY, rightContentW, rightTopH), toolCalls);
            DrawTodoPanel(
                new Rect(rightX, panelsY + rightTopH + rightGap, rightContentW, rightBottomH));

            // Input
            float inputY = panelsY + panelsH + gap;
            DrawInputRow(new Rect(inRect.x, inputY, inRect.width, inputH));

            // Footer
            float footerY = inputY + inputH + gap;
            DrawFooter(new Rect(inRect.x, footerY, inRect.width, footerH));
        }

        // ========== 预算横幅 ==========

        /// <summary>绘制预算警告横幅，返回实际高度（0=不显示）</summary>
        private static float DrawBudgetBanner(Rect rect)
        {
            var status = ChatDisplayState.CurrentBudgetStatus;
            if (status == BudgetStatus.Ok) return 0;

            string text;
            Color bgColor;
            switch (status)
            {
                case BudgetStatus.Warning:
                    text = $"Token 预算已用 {ChatDisplayState.CurrentBudgetPercent:F0}%，请注意控制";
                    bgColor = new Color(0.55f, 0.45f, 0.1f, _alpha);
                    break;
                case BudgetStatus.Critical:
                    text = $"Token 预算即将用尽 {ChatDisplayState.CurrentBudgetPercent:F0}%";
                    bgColor = new Color(0.55f, 0.25f, 0.1f, _alpha);
                    break;
                case BudgetStatus.Exceeded:
                    var settings = RimWorldMCPMod.Instance.Settings;
                    if (settings.TokenBudgetExceedAction == TokenBudgetExceedAction.Block)
                        text = "Token 预算已用尽，游戏已暂停。请提高预算或开始新游戏。";
                    else
                        text = "Token 预算已用尽（仅警告模式），已发送通知。";
                    bgColor = new Color(0.55f, 0.15f, 0.15f, _alpha);
                    break;
                default:
                    return 0;
            }

            Widgets.DrawBoxSolid(rect, bgColor);
            Text.Font = GameFont.Tiny;
            var oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.white;
            Widgets.Label(rect, text);
            GUI.color = Color.white;
            Text.Anchor = oldAnchor;
            Text.Font = GameFont.Small;
            return rect.height;
        }

        // ========== 顶栏 ==========

        private static void DrawHeader(Rect rect)
        {
            string colony = "未知殖民地";
            try
            {
                var map = Find.CurrentMap;
                if (map != null)
                {
                    var parent = map.Parent;
                    if (parent != null && !parent.Label.NullOrEmpty())
                        colony = parent.Label;
                    else if (Find.World?.info?.name != null)
                        colony = Find.World.info.name;
                }
            }
            catch { }

            string dayInfo = "";
            try
            {
                var map = Find.CurrentMap;
                if (map != null)
                {
                    var season = GenLocalDate.Season(map);
                    string seasonName = season switch { Season.Spring => "春", Season.Summer => "夏",
                        Season.Fall => "秋", Season.Winter => "冬", _ => season.ToString() };
                    int dayOfQ = GenLocalDate.DayOfQuadrum(map);
                    int year = GenLocalDate.Year(map);
                    dayInfo = $" · {year}年 {seasonName}第{dayOfQ}天";
                }
            }
            catch { }

            string header = $"{colony}{dayInfo}";
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f, _alpha);
            Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width * 0.55f, rect.height - 2f), header);
            GUI.color = Color.white;

            // Token 消耗右对齐
            string tokenText = TokenUsageTracker.GetCompactDisplay(RimWorldMCPMod.Instance.Settings.TokenBudgetLimit);
            float tokenW = Text.CalcSize(tokenText).x;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.5f, 0.55f, 0.65f, _alpha);
            Widgets.Label(new Rect(rect.xMax - tokenW - 4f, rect.y + 2f, tokenW, rect.height - 2f), tokenText);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 底部分隔线
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax, rect.width, 1f),
                new Color(0.18f, 0.18f, 0.20f, _alpha));
        }

        // ========== 左栏：对话流 ==========

        private void DrawConversationPanel(Rect panelRect, List<ChatEntry> entries)
        {
            // 标题
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.4f, 0.4f, 0.42f, _alpha);
            Widgets.Label(new Rect(panelRect.x, panelRect.y, 100f, 16f), "对话");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect scrollRect = new Rect(panelRect.x, panelRect.y + 14f,
                panelRect.width, panelRect.height - 14f);

            if (entries.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.35f, 0.35f, 0.35f, _alpha);
                Widgets.Label(new Rect(scrollRect.x, scrollRect.y + 4f,
                    scrollRect.width, 16f), "等待 AI 回应...");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            float contentWidth = scrollRect.width - 16f;
            float totalH = 4f;
            foreach (var entry in entries)
            {
                CalcEntryHeight(entry, contentWidth);
                totalH += entry.CachedHeight + 6f;
            }

            bool isStreaming = entries.Count > 0
                && entries[entries.Count - 1].State == ChatState.Streaming;

            float maxScroll = Mathf.Max(0f, totalH - scrollRect.height);

            // 磁吸：检测上帧是否在底部（用旧 maxScroll 避免内容增长导致误断）
            if (_lastMaxScroll >= 0f)
            {
                bool wasAtBottom = _chatScrollPos.y >= _lastMaxScroll - 4f;
                if (!wasAtBottom && _chatScrollPos.y < maxScroll - 4f) _chatUserScrolledUp = true;
                if (_chatScrollPos.y >= maxScroll - 2f) _chatUserScrolledUp = false;
                if (wasAtBottom && isStreaming) _chatUserScrolledUp = false; // 内容增长时保持在底
            }
            if (!isStreaming) { _chatUserScrolledUp = false; _lastMaxScroll = -1f; }
            else _lastMaxScroll = maxScroll;

            if ((isStreaming && !_chatUserScrolledUp) || (_scrollToBottom && !_chatUserScrolledUp))
            {
                _chatScrollPos.y = maxScroll;
                _scrollToBottom = false;
            }

            // 滚轮
            if (Event.current.type == EventType.ScrollWheel && Mouse.IsOver(scrollRect))
            {
                _chatScrollPos.y += Event.current.delta.y * 20f;
                _chatScrollPos.y = Mathf.Clamp(_chatScrollPos.y, 0f, maxScroll);
                Event.current.Use();
            }
            _chatScrollPos.y = Mathf.Clamp(_chatScrollPos.y, 0f, maxScroll);

            // 手动裁剪 + 滚动，绕过 BeginScrollView 的闪烁问题
            GUI.BeginGroup(scrollRect);
            float curY = 4f - _chatScrollPos.y;
            foreach (var entry in entries)
            {
                float entryH = entry.CachedHeight + 6f;
                if (curY + entryH > 0f && curY < scrollRect.height)
                    DrawEntry(entry, contentWidth, curY);
                curY += entryH;
            }
            GUI.EndGroup();

            // 手绘滚动条
            if (maxScroll > 0f)
            {
                float barW = 6f;
                float barX = scrollRect.xMax - barW - 2f;
                float barAreaH = scrollRect.height;
                float barH = Mathf.Max(barAreaH * barAreaH / totalH, 16f);
                float barY = scrollRect.y + _chatScrollPos.y * barAreaH / totalH;
                Rect barRect = new Rect(barX, barY, barW, barH);
                Widgets.DrawBoxSolid(barRect, new Color(0.35f, 0.35f, 0.35f, 0.6f));
            }
        }

        // ========== 右栏：工具调用卡片 ==========

        private void DrawToolPanel(Rect panelRect, List<ToolCallInfo> toolCalls)
        {
            // 标题
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.4f, 0.4f, 0.42f, _alpha);
            string title = $"工具调用 ({toolCalls.Count})";
            Widgets.Label(new Rect(panelRect.x, panelRect.y, 120f, 16f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect scrollRect = new Rect(panelRect.x, panelRect.y + 14f,
                panelRect.width, panelRect.height - 14f);

            if (toolCalls.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.35f, 0.35f, 0.35f, _alpha);
                Widgets.Label(new Rect(scrollRect.x, scrollRect.y + 4f,
                    scrollRect.width, 16f), "暂无工具调用");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            float cardWidth = scrollRect.width - 16f;
            float totalH = 4f;
            foreach (var tc in toolCalls)
                totalH += CalcCardHeight(tc, cardWidth) + 6f;

            bool hasRunning = false;
            foreach (var tc in toolCalls)
                if (tc.Status == ToolStatus.Running) { hasRunning = true; break; }

            float toolMaxScroll = Mathf.Max(0f, totalH - scrollRect.height);

            // 磁吸
            if (_lastToolMaxScroll >= 0f)
            {
                bool wasAtBottom = _toolScrollPos.y >= _lastToolMaxScroll - 4f;
                if (!wasAtBottom && _toolScrollPos.y < toolMaxScroll - 4f) _toolUserScrolledUp = true;
                if (_toolScrollPos.y >= toolMaxScroll - 2f) _toolUserScrolledUp = false;
                if (wasAtBottom && hasRunning) _toolUserScrolledUp = false;
            }
            if (!hasRunning) { _toolUserScrolledUp = false; _lastToolMaxScroll = -1f; }
            else _lastToolMaxScroll = toolMaxScroll;

            if ((hasRunning && !_toolUserScrolledUp) || (_toolScrollToBottom && !_toolUserScrolledUp))
            {
                _toolScrollPos.y = toolMaxScroll;
                _toolScrollToBottom = false;
            }

            // 滚轮
            if (Event.current.type == EventType.ScrollWheel && Mouse.IsOver(scrollRect))
            {
                _toolScrollPos.y += Event.current.delta.y * 20f;
                _toolScrollPos.y = Mathf.Clamp(_toolScrollPos.y, 0f, toolMaxScroll);
                Event.current.Use();
            }
            _toolScrollPos.y = Mathf.Clamp(_toolScrollPos.y, 0f, toolMaxScroll);

            // 手动裁剪 + 滚动
            GUI.BeginGroup(scrollRect);
            float curY = 4f - _toolScrollPos.y;
            for (int i = 0; i < toolCalls.Count; i++)
            {
                float cardH = CalcCardHeight(toolCalls[i], cardWidth) + 6f;
                if (curY + cardH > 0f && curY < scrollRect.height)
                    DrawToolCard(toolCalls[i], i, cardWidth, curY);
                curY += cardH;
            }
            GUI.EndGroup();

            // 手绘滚动条
            if (toolMaxScroll > 0f)
            {
                float tBarW = 6f;
                float tBarX = scrollRect.xMax - tBarW - 2f;
                float tBarAreaH = scrollRect.height;
                float tBarH = Mathf.Max(tBarAreaH * tBarAreaH / totalH, 16f);
                float tBarY = scrollRect.y + _toolScrollPos.y * tBarAreaH / totalH;
                Rect tBarRect = new Rect(tBarX, tBarY, tBarW, tBarH);
                Widgets.DrawBoxSolid(tBarRect, new Color(0.35f, 0.35f, 0.35f, 0.6f));
            }
        }

        private static float CalcCardHeight(ToolCallInfo tc, float width)
        {
            string name = ToolDisplayNames.GetDisplayName(tc.Name ?? "").Replace("_", "__");
            if (string.IsNullOrEmpty(name)) name = tc.Name ?? "?";
            float headerH = Text.CalcHeight(name, width - 12f) + 6f;

            float bodyH = 0f;
            if (!string.IsNullOrEmpty(tc.Meta))
                bodyH = Text.CalcHeight(tc.Meta, width - 12f) + 4f;

            return headerH + bodyH + 10f;
        }

        private static float DrawToolCard(ToolCallInfo tc, int index, float width, float y)
        {
            string name = ToolDisplayNames.GetDisplayName(tc.Name ?? "").Replace("_", "__");
            if (string.IsNullOrEmpty(name)) name = tc.Name ?? "?";
            float headerH = Text.CalcHeight(name, width - 12f) + 6f;
            float bodyH = 0f;
            if (!string.IsNullOrEmpty(tc.Meta))
                bodyH = Text.CalcHeight(tc.Meta, width - 12f) + 4f;
            float cardH = headerH + bodyH + 10f;

            Rect cardRect = new Rect(2f, y, width, cardH);
            Color bgColor = tc.Status == ToolStatus.Failed
                ? new Color(0.18f, 0.06f, 0.06f, _alpha)
                : ToolCardBg;
            Widgets.DrawBoxSolid(cardRect, bgColor);

            // Card header
            Rect headerRect = new Rect(cardRect.x, cardRect.y, cardRect.width, headerH + 4f);
            Widgets.DrawBoxSolid(headerRect, ToolCardHeaderBg);

            // 失败卡片加红左边框
            if (tc.Status == ToolStatus.Failed)
            {
                Widgets.DrawBoxSolid(new Rect(cardRect.x, cardRect.y, 2f, cardRect.height),
                    new Color(0.8f, 0.2f, 0.2f, _alpha));
            }

            string statusIcon = tc.Status == ToolStatus.Running ? "◎"
                : tc.Status == ToolStatus.Completed ? "✓" : "✗";
            Color statusColor = tc.Status == ToolStatus.Running
                ? new Color(1f, 0.75f, 0.3f)
                : tc.Status == ToolStatus.Completed
                    ? new Color(0.3f, 0.9f, 0.3f)
                    : new Color(1f, 0.3f, 0.3f);

            // Index + status + name（左）+ 耗时（右）
            string headerText = $"#{index + 1} {statusIcon} {name}";
            float durW = 0f;
            string durText = "";
            if (tc.Status != ToolStatus.Running)
            {
                durText = FormatDuration(tc.DurationMs);
                durW = Text.CalcSize(durText).x + 4f;
            }
            Text.Font = GameFont.Tiny;
            GUI.color = statusColor;
            Widgets.Label(new Rect(headerRect.x + 4f, headerRect.y + 2f,
                headerRect.width - 8f - durW, headerH), headerText);
            if (durW > 0f)
            {
                GUI.color = new Color(0.4f, 0.4f, 0.4f, _alpha);
                Widgets.Label(new Rect(headerRect.xMax - durW - 2f, headerRect.y + 2f,
                    durW, headerH), durText);
            }
            GUI.color = Color.white;

            // Body (meta)
            if (!string.IsNullOrEmpty(tc.Meta))
            {
                float bodyY = headerRect.yMax + 2f;
                Text.Font = GameFont.Tiny;
                GUI.color = tc.Status == ToolStatus.Failed
                    ? new Color(0.9f, 0.4f, 0.4f, _alpha)
                    : new Color(0.55f, 0.55f, 0.6f, _alpha);
                Widgets.Label(new Rect(cardRect.x + 6f, bodyY,
                    cardRect.width - 12f, bodyH), tc.Meta);
                GUI.color = Color.white;
            }

            Text.Font = GameFont.Small;
            return cardH;
        }

        private static string FormatDuration(double ms)
        {
            if (ms < 1000) return $"{(int)ms}ms";
            if (ms < 60000) return $"{ms / 1000:F1}s";
            return $"{(int)(ms / 60000)}m {((int)(ms / 1000)) % 60}s";
        }

        // ========== TODO 面板 ==========

        private void DrawTodoPanel(Rect panelRect)
        {
            var items = TodoManager.Query(null);
            int pendingCount = 0;
            foreach (var i in items) { if (i.Status != "done") pendingCount++; }

            // 标题
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.4f, 0.4f, 0.42f, _alpha);
            Widgets.Label(new Rect(panelRect.x, panelRect.y, 120f, 16f), $"TODO ({pendingCount})");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect scrollRect = new Rect(panelRect.x, panelRect.y + 14f,
                panelRect.width, panelRect.height - 14f);

            if (items.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.35f, 0.35f, 0.35f, _alpha);
                Widgets.Label(new Rect(scrollRect.x, scrollRect.y + 4f,
                    scrollRect.width, 16f), "暂无待办事项");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            float itemH = 20f;
            float totalH = items.Count * (itemH + 2f) + 4f;
            float contentW = scrollRect.width - 16f;

            Rect viewRect = new Rect(0f, 0f, contentW, Mathf.Max(totalH, scrollRect.height));
            Widgets.BeginScrollView(scrollRect, ref _todoScrollPos, viewRect);

            float curY = 2f;
            for (int idx = 0; idx < items.Count; idx++)
            {
                var item = items[idx];
                bool isDone = item.Status == "done";

                Color prioColor = item.Priority >= 4
                    ? new Color(0.7f, 0.35f, 0.35f)
                    : item.Priority >= 2
                        ? new Color(0.7f, 0.6f, 0.35f)
                        : new Color(0.45f, 0.45f, 0.45f);
                Color textColor = isDone ? new Color(0.35f, 0.35f, 0.35f) : new Color(0.75f, 0.75f, 0.75f);

                Rect rowRect = new Rect(2f, curY, contentW - 4f, itemH);
                if (idx % 2 == 0)
                    Widgets.DrawBoxSolid(rowRect, new Color(0.1f, 0.1f, 0.14f, 0.5f));

                string prioMark = $"P{item.Priority}";
                Text.Font = GameFont.Tiny;
                GUI.color = prioColor;
                Widgets.Label(new Rect(rowRect.x + 2f, rowRect.y + 2f, 22f, 16f), prioMark);

                GUI.color = textColor;
                Widgets.Label(new Rect(rowRect.x + 26f, rowRect.y + 2f, rowRect.width - 70f, 16f),
                    item.Description ?? "");

                GUI.color = new Color(0.35f, 0.35f, 0.35f);
                Widgets.Label(new Rect(rowRect.x + rowRect.width - 44f, rowRect.y + 2f, 42f, 16f),
                    $"#{item.Id}");

                curY += itemH + 2f;
            }

            Widgets.EndScrollView();

            if (_todoScrollToBottom)
            {
                _todoScrollPos.y = Mathf.Max(0f, viewRect.height - scrollRect.height);
                _todoScrollToBottom = false;
            }

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // ========== 输入行 ==========

        private void DrawInputRow(Rect rect)
        {
            float btnW = 56f;
            float gap = 4f;
            float padX = 2f;

            Rect tfRect = new Rect(rect.x + padX, rect.y + 2f,
                rect.width - btnW - gap - padX * 2, rect.height - 4f);
            GUI.color = Color.white;
            GUI.SetNextControlName("chatInput");
            _inputText = Widgets.TextField(tfRect, _inputText);

            Rect sendRect = new Rect(tfRect.xMax + gap, rect.y + 2f, btnW, rect.height - 4f);
            if (Widgets.ButtonText(sendRect, "发送"))
                TrySendInput();

            GUI.color = Color.white;
        }

        // ========== 底栏 ==========

        private void DrawFooter(Rect rect)
        {
            bool connected = CCClient.IsReady;
            float btnW = 22f;
            float btnH = rect.height - 4f;
            float y = rect.y + 2f;

            // 连接状态
            float statusX = rect.x + 2f;
            Rect statusRect = new Rect(statusX, y, 70f, btnH);
            Text.Font = GameFont.Tiny;
            GUI.color = connected ? new Color(0.4f, 0.55f, 0.4f, _alpha) : new Color(0.7f, 0.35f, 0.35f, _alpha);
            Widgets.Label(statusRect, connected ? "● 已连接" : "● 未连接");
            GUI.color = Color.white;

            // 工具计数
            float toolsX = statusX + 72f;
            var toolCount = ChatDisplayState.ToolCallsSnapshot.Count;
            Rect toolsRect = new Rect(toolsX, y, 70f, btnH);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.45f, 0.45f, 0.45f, _alpha);
            Widgets.Label(toolsRect, $"Tools: {toolCount}");
            GUI.color = Color.white;

            // 透明度
            float alphaLabelX = toolsX + 75f;
            Rect alphaLabel = new Rect(alphaLabelX, y, 40f, btnH);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.4f, 0.4f, 0.4f, _alpha);
            Widgets.Label(alphaLabel, "透明");
            GUI.color = Color.white;

            Rect alphaMinus = new Rect(alphaLabelX + 28f, y, btnW, btnH);
            if (Widgets.ButtonText(alphaMinus, "-"))
                _alpha = Mathf.Clamp(_alpha - 0.1f, 0.2f, 1f);

            Rect alphaPlus = new Rect(alphaLabelX + 28f + btnW + 2f, y, btnW, btnH);
            if (Widgets.ButtonText(alphaPlus, "+"))
                _alpha = Mathf.Clamp(_alpha + 0.1f, 0.2f, 1f);

            // 威胁摘要（仅 emoji）
            string danger = BridgeLifecycle.DangerSummary;
            if (!string.IsNullOrEmpty(danger))
            {
                float dangerX = alphaPlus.xMax + 8f;
                float dangerW = rect.xMax - dangerX - 230f;
                if (dangerW > 60f)
                {
                    // 去掉"待处理:"前缀，只保留 emoji
                    string shortText = danger.Replace("待处理: ", "");
                    Rect dangerRect = new Rect(dangerX, y, dangerW, btnH);
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 0.6f, 0.3f, _alpha);
                    Widgets.Label(dangerRect, shortText);
                    GUI.color = Color.white;
                }
            }

            // 右侧按钮：清空 | 继续 | 中断
            float rightSide = rect.xMax;
            float actionBtnW = 52f;
            float actionBtnH = btnH;

            Rect abortRect = new Rect(rightSide - actionBtnW, y, actionBtnW, actionBtnH);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(abortRect, "中断"))
            {
                ChatDisplayState.MarkLastAborted();
                _ = CCClient.SendAbort();
            }

            Rect continueRect = new Rect(abortRect.x - actionBtnW - 4f, y, actionBtnW, actionBtnH);
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

            Rect clearTodoRect = new Rect(continueRect.x - 52f - 4f, y, 52f, actionBtnH);
            GUI.color = TodoManager.Count > 0 ? Color.white : Color.grey;
            if (Widgets.ButtonText(clearTodoRect, "清TODO"))
            {
                if (TodoManager.Count > 0)
                    TodoManager.Clear();
            }

            Rect clearRect = new Rect(clearTodoRect.x - 44f - 4f, y, 44f, actionBtnH);
            GUI.color = Color.white;
            if (Widgets.ButtonText(clearRect, "清空"))
                ChatDisplayState.Clear();

            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // ========== 对话条目 ==========

        private static void CalcEntryHeight(ChatEntry entry, float contentWidth)
        {
            var thinking = (entry.ThinkingText ?? "").Replace("_", "__");
            var body = (entry.Text ?? "").Replace("_", "__");
            bool streaming = entry.State == ChatState.Streaming;
            bool changed = body.Length != entry.CachedTextLen
                        || thinking.Length != entry.CachedThinkingLen;
            if (!changed && entry.CachedHeight > 0f) return;

            float textAreaW = contentWidth - 32f;
            bool cursorOnThinking = !string.IsNullOrEmpty(thinking) && streaming;
            bool cursorOnBody = !cursorOnThinking && streaming;

            float totalH = 0f;
            if (!string.IsNullOrEmpty(thinking) || cursorOnThinking)
                totalH += Text.CalcHeight((thinking + (cursorOnThinking ? " " : "")).StripTags(), textAreaW);
            if (!string.IsNullOrEmpty(body) || cursorOnBody)
                totalH += Text.CalcHeight((body + (cursorOnBody ? " " : "")).StripTags(), textAreaW);

            float newH = 25f + Mathf.Max(totalH, 10f);
            // 流式时预分配 2 行 + 只增不减，避免换行瞬时高度突变闪烁
            if (streaming)
            {
                newH += 14f; // 预留 1 行缓冲
                if (newH < entry.CachedHeight) newH = entry.CachedHeight;
            }
            entry.CachedHeight = newH;
            entry.CachedTextLen = body.Length;
            entry.CachedThinkingLen = thinking.Length;
        }

        private static float DrawEntry(ChatEntry entry, float contentWidth, float y)
        {
            bool isSubagent = !string.IsNullOrEmpty(entry.AgentId);
            bool isThinking = !string.IsNullOrEmpty(entry.ThinkingText);
            string label = entry.IsContext ? "系统"
                : entry.Role == ChatRole.User ? "你"
                : isSubagent ? entry.AgentType
                : isThinking ? "AI 思考中" : "AI";

            string body = (entry.Text ?? "").Replace("_", "__");
            string thinking = (entry.ThinkingText ?? "").Replace("_", "__");
            bool streaming = entry.State == ChatState.Streaming;
            string cursor = streaming && Time.realtimeSinceStartup % 1.0f < 0.6f ? "▌" : " ";

            float bodyWidth = contentWidth - 20f;
            float textAreaW = bodyWidth - 12f;
            float entryHeight = entry.CachedHeight;

            Rect bubbleRect = new Rect(2f, y, contentWidth, entryHeight);
            Color bgColor = entry.IsContext ? new Color(0.12f, 0.12f, 0.18f, 1f)
                : entry.Role == ChatRole.User ? UserBgColor
                : entry.State == ChatState.Error ? ErrorBgColor
                : isSubagent ? SubagentBgColor : AiBgColor;
            bgColor.a = _alpha;
            Widgets.DrawBoxSolid(bubbleRect, bgColor);

            // 右键复制
            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 1
                && Mouse.IsOver(bubbleRect))
            {
                GUIUtility.systemCopyBuffer = entry.Text;
                Messages.Message("已复制到剪贴板", MessageTypeDefOf.SilentInput, false);
                Event.current.Use();
            }

            // 标签
            Text.Font = GameFont.Small;
            float labelW = Text.CalcSize(label).x + 4f;
            Rect labelRect = new Rect(bubbleRect.x + 6f, bubbleRect.y + 3f, labelW, 20f);
            GUI.color = entry.IsContext ? new Color(0.5f, 0.5f, 0.6f, _alpha)
                : entry.Role == ChatRole.User
                    ? new Color(0.5f, 0.55f, 0.65f, _alpha)
                    : isSubagent
                        ? new Color(0.6f, 0.45f, 0.65f, _alpha)
                        : isThinking
                            ? new Color(0.7f, 0.6f, 0.35f, _alpha)
                            : new Color(0.45f, 0.55f, 0.45f, _alpha);
            Widgets.Label(labelRect, label);

            float curY = labelRect.yMax + 2f;

            // 光标挂在最后活跃的文本块上
            bool cursorOnThinking = isThinking && streaming;
            bool cursorOnBody = !isThinking && streaming;

            // 思考文本（暗黄色）
            if (!string.IsNullOrEmpty(thinking) || cursorOnThinking)
            {
                var t = (thinking ?? "") + (cursorOnThinking ? cursor : "");
                float h = Text.CalcHeight(t.StripTags(), textAreaW);
                Rect r = new Rect(bubbleRect.x + 8f, curY, textAreaW, h);
                GUI.color = new Color(0.5f, 0.48f, 0.35f, _alpha);
                Text.Font = GameFont.Small;
                Widgets.Label(r, t);
                curY += h;
            }

            // 正文（白色）
            if (!string.IsNullOrEmpty(body) || cursorOnBody)
            {
                var t = (body ?? "") + (cursorOnBody ? cursor : "");
                float h = Text.CalcHeight(t.StripTags(), textAreaW);
                Rect r = new Rect(bubbleRect.x + 8f, curY, textAreaW, Mathf.Max(h, 10f));
                GUI.color = entry.IsContext ? new Color(0.55f, 0.55f, 0.6f, _alpha)
                    : new Color(0.85f, 0.85f, 0.85f, _alpha);
                Text.Font = GameFont.Small;
                Widgets.Label(r, t);
            }

            GUI.color = Color.white;
            return entryHeight;
        }
    }
}
