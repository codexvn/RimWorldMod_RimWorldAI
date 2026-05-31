using System;
using System.Collections.Generic;
using RimWorld;
using RimWorldAgent.Core.AgentRuntime;
using RimWorldAgent.Core.CcbManager;
using UnityEngine;
using Verse;

namespace RimWorldAgent
{
    /// <summary>
    /// AI 对话窗口 — 双栏布局：左栏对话流，右栏 SDK 任务列表
    /// </summary>
    public class Dialog_AiChat : Window
    {
        private Vector2 _chatScrollPos;
        private Vector2 _taskScrollPos;
        private bool _scrollToBottom;
        private string _inputText = "";
        private static float _alpha = 0.85f;

        private static readonly Color UserBgColor = new Color(0.18f, 0.20f, 0.24f, 1f);
        private static readonly Color AiBgColor = new Color(0.14f, 0.16f, 0.18f, 1f);
        private static readonly Color SubagentBgColor = new Color(0.16f, 0.14f, 0.20f, 1f);
        private static readonly Color ErrorBgColor = new Color(0.24f, 0.12f, 0.12f, 1f);

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
            _chatUserScrolledUp = false;
            _scrollToBottom = true;
        }

        public override void PostClose()
        {
            ChatDisplayState.OnChanged -= OnChatChanged;
            base.PostClose();
        }

        private int _lastChatCount;
        private bool _chatUserScrolledUp;
        private float _lastMaxScroll = -1f;

        private void OnChatChanged()
        {
            var snap = ChatDisplayState.Snapshot;
            if (snap.Count != _lastChatCount) _scrollToBottom = true;
            _lastChatCount = snap.Count;
        }

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
            var sdkTasks = ToolDispatcher.TasksSnapshot();

            float headerH = 22f;
            float inputH = 28f;
            float footerH = 22f;
            float gap = 4f;
            float panelGap = 6f;
            float leftRatio = 0.55f;

            // Header
            DrawHeader(new Rect(inRect.x, inRect.y, inRect.width, headerH));

            // 预算横幅
            float bannerH = DrawBudgetBanner(new Rect(inRect.x, inRect.y + headerH + gap,
                inRect.width, 22f));
            float bannerOffset = bannerH > 0 ? bannerH + gap : 0;

            // Panels
            float panelsY = inRect.y + headerH + gap + bannerOffset;
            float panelsHEx = inRect.height - headerH - inputH - footerH - gap * 3;
            float panelsY2 = panelsY + panelsHEx; // use raw Y
            float leftW = (inRect.width - panelGap) * leftRatio;
            float rightW = inRect.width - leftW - panelGap;

            // 分隔线
            float dividerX = inRect.x + leftW + panelGap / 2f;
            Widgets.DrawBoxSolid(new Rect(dividerX, panelsY, 1f, panelsHEx),
                new Color(0.22f, 0.22f, 0.24f, _alpha));

            float rightX = dividerX + panelGap / 2f + 1f;
            float rightContentW = rightW - 2f;

            DrawConversationPanel(
                new Rect(inRect.x, panelsY, leftW, panelsHEx), entries);
            DrawTaskPanel(
                new Rect(rightX, panelsY, rightContentW, panelsHEx), sdkTasks);

            // Input
            float inputY = panelsY + panelsHEx + gap;
            DrawInputRow(new Rect(inRect.x, inputY, inRect.width, inputH));

            // Footer
            float footerY = inputY + inputH + gap;
            DrawFooter(new Rect(inRect.x, footerY, inRect.width, footerH));
        }

        // ========== 预算横幅 ==========

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
                    text = "Token 预算已用尽";
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
            catch (Exception ex) { Log.Warning($"[AiChat] 读取殖民地名称失败: {ex.Message}"); }

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
            catch (Exception ex) { Log.Warning($"[AiChat] 读取日期信息失败: {ex.Message}"); }

            // 左：殖民地 + 日期 + 模型
            string model = TokenUsageTracker.CurrentModel;
            if (!string.IsNullOrEmpty(model))
            {
                int slash = model.LastIndexOf('/');
                model = slash >= 0 ? model.Substring(slash + 1) : model;
            }
            string header = $"{colony}{dayInfo}{(string.IsNullOrEmpty(model) ? "" : $" · {model}")}";
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f, _alpha);
            Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width * 0.5f, rect.height - 2f), header);
            GUI.color = Color.white;

            // 右上：阶段/角色
            string statusText = AgentOrchestrator.StatusText;
            Color statusColor = AgentOrchestrator.CurrentPhase == GamePhase.Plan
                ? new Color(0.55f, 0.5f, 0.35f, _alpha)
                : AgentOrchestrator.CurrentPhase == GamePhase.Act
                    ? new Color(0.35f, 0.55f, 0.35f, _alpha)
                    : new Color(0.45f, 0.45f, 0.45f, _alpha);
            float statusW = Text.CalcSize(statusText).x;
            Text.Font = GameFont.Tiny;
            GUI.color = statusColor;
            Widgets.Label(new Rect(rect.xMax - statusW - 4f, rect.y + 2f, statusW, rect.height - 2f), statusText);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // 底部分隔线
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax, rect.width, 1f),
                new Color(0.18f, 0.18f, 0.20f, _alpha));
        }

        // ========== 左栏：对话流 ==========

        private void DrawConversationPanel(Rect panelRect, List<ChatEntry> entries)
        {
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

            if (_lastMaxScroll >= 0f)
            {
                bool wasAtBottom = _chatScrollPos.y >= _lastMaxScroll - 4f;
                if (!wasAtBottom && _chatScrollPos.y < maxScroll - 4f) _chatUserScrolledUp = true;
                if (_chatScrollPos.y >= maxScroll - 2f) _chatUserScrolledUp = false;
                if (wasAtBottom && isStreaming) _chatUserScrolledUp = false;
            }
            if (!isStreaming) { _chatUserScrolledUp = false; _lastMaxScroll = -1f; }
            else _lastMaxScroll = maxScroll;

            if ((isStreaming && !_chatUserScrolledUp) || (_scrollToBottom && !_chatUserScrolledUp))
            {
                _chatScrollPos.y = maxScroll;
                _scrollToBottom = false;
            }

            if (Event.current.type == EventType.ScrollWheel && Mouse.IsOver(scrollRect))
            {
                _chatScrollPos.y += Event.current.delta.y * 20f;
                _chatScrollPos.y = Mathf.Clamp(_chatScrollPos.y, 0f, maxScroll);
                Event.current.Use();
            }
            _chatScrollPos.y = Mathf.Clamp(_chatScrollPos.y, 0f, maxScroll);

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

        // ========== 右栏：SDK 任务列表 ==========

        private void DrawTaskPanel(Rect panelRect, List<ToolDispatcher.TaskItem> tasks)
        {
            int pending = 0;
            int completed = 0;
            foreach (var t in tasks)
            {
                if (t.Status == "completed") completed++;
                else pending++;
            }

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.4f, 0.4f, 0.42f, _alpha);
            string title = $"AI 计划 ({pending}/{tasks.Count})";
            Widgets.Label(new Rect(panelRect.x, panelRect.y, 160f, 16f), title);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect scrollRect = new Rect(panelRect.x, panelRect.y + 14f,
                panelRect.width, panelRect.height - 14f);

            if (tasks.Count == 0)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.35f, 0.35f, 0.35f, _alpha);
                Widgets.Label(new Rect(scrollRect.x, scrollRect.y + 4f,
                    scrollRect.width, 16f), "暂无计划");
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            float itemW = scrollRect.width - 16f;
            float itemH = 18f;
            float totalH = 4f + tasks.Count * (itemH + 3f);

            float maxScroll = Mathf.Max(0f, totalH - scrollRect.height);
            if (Event.current.type == EventType.ScrollWheel && Mouse.IsOver(scrollRect))
            {
                _taskScrollPos.y += Event.current.delta.y * 20f;
                _taskScrollPos.y = Mathf.Clamp(_taskScrollPos.y, 0f, maxScroll);
                Event.current.Use();
            }
            _taskScrollPos.y = Mathf.Clamp(_taskScrollPos.y, 0f, maxScroll);

            GUI.BeginGroup(scrollRect);
            float curY = 4f - _taskScrollPos.y;
            for (int i = 0; i < tasks.Count; i++)
            {
                if (curY + itemH > 0f && curY < scrollRect.height)
                    DrawTaskItem(tasks[i], i, itemW, curY, itemH);
                curY += itemH + 3f;
            }
            GUI.EndGroup();

            if (maxScroll > 0f)
            {
                float barW = 6f;
                float barX = scrollRect.xMax - barW - 2f;
                float barAreaH = scrollRect.height;
                float barH = Mathf.Max(barAreaH * barAreaH / totalH, 16f);
                float barY = scrollRect.y + _taskScrollPos.y * barAreaH / totalH;
                Widgets.DrawBoxSolid(new Rect(barX, barY, barW, barH),
                    new Color(0.35f, 0.35f, 0.35f, 0.6f));
            }
        }

        private static float DrawTaskItem(ToolDispatcher.TaskItem task, int index, float width, float y, float height)
        {
            Color statusColor = task.Status switch
            {
                "in_progress" => new Color(0.9f, 0.7f, 0.25f, _alpha),
                "completed" => new Color(0.3f, 0.8f, 0.3f, _alpha),
                _ => new Color(0.45f, 0.45f, 0.45f, _alpha)
            };
            string icon = task.Status switch
            {
                "in_progress" => "▶",
                "completed" => "✓",
                _ => "○"
            };

            Rect itemRect = new Rect(2f, y, width, height);

            Text.Font = GameFont.Tiny;
            // 状态图标
            GUI.color = statusColor;
            Widgets.Label(new Rect(itemRect.x + 2f, itemRect.y + 1f, 16f, height), icon);

            // 标题
            string label = task.Subject.Length > 40 ? task.Subject.Substring(0, 38) + "…" : task.Subject;
            GUI.color = task.Status == "completed"
                ? new Color(0.4f, 0.4f, 0.4f, _alpha)
                : new Color(0.8f, 0.8f, 0.8f, _alpha);
            Widgets.Label(new Rect(itemRect.x + 18f, itemRect.y + 1f, width - 20f, height), label);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            return height;
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

            // Token 使用（精简版）
            string tokenText = TokenUsageTracker.GetCompactDisplay(0);
            float tokenW = Text.CalcSize(tokenText).x + 8f;
            Rect tokenRect = new Rect(statusX + 72f, y, Mathf.Min(tokenW + 20f, 200f), btnH);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.5f, 0.55f, 0.65f, _alpha);
            Widgets.Label(tokenRect, tokenText);
            GUI.color = Color.white;

            // 透明度
            float alphaX = tokenRect.xMax + 8f;
            Rect alphaLabel = new Rect(alphaX, y, 30f, btnH);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.4f, 0.4f, 0.4f, _alpha);
            Widgets.Label(alphaLabel, "透明");
            GUI.color = Color.white;

            Rect alphaMinus = new Rect(alphaX + 24f, y, btnW, btnH);
            if (Widgets.ButtonText(alphaMinus, "-"))
                _alpha = Mathf.Clamp(_alpha - 0.1f, 0.2f, 1f);

            Rect alphaPlus = new Rect(alphaX + 24f + btnW + 2f, y, btnW, btnH);
            if (Widgets.ButtonText(alphaPlus, "+"))
                _alpha = Mathf.Clamp(_alpha + 0.1f, 0.2f, 1f);

            // 威胁摘要
            string danger = BridgeLifecycle.DangerSummary;
            if (!string.IsNullOrEmpty(danger))
            {
                float dangerX = alphaPlus.xMax + 8f;
                float dangerW = rect.xMax - dangerX - 230f;
                if (dangerW > 40f)
                {
                    string shortText = danger.Replace("待处理: ", "");
                    Rect dangerRect = new Rect(dangerX, y, dangerW, btnH);
                    Text.Font = GameFont.Tiny;
                    GUI.color = new Color(1f, 0.6f, 0.3f, _alpha);
                    Widgets.Label(dangerRect, shortText);
                    GUI.color = Color.white;
                }
            }

            // 右侧按钮
            float rightSide = rect.xMax;
            float actionBtnW = 52f;

            Rect abortRect = new Rect(rightSide - actionBtnW, y, actionBtnW, btnH);
            GUI.color = connected ? Color.white : Color.grey;
            if (Widgets.ButtonText(abortRect, "中断"))
            {
                ChatDisplayState.MarkLastAborted();
                _ = CCClient.SendAbort();
            }

            Rect continueRect = new Rect(abortRect.x - actionBtnW - 4f, y, actionBtnW, btnH);
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

            Rect clearRect = new Rect(continueRect.x - 44f - 4f, y, 44f, btnH);
            GUI.color = Color.white;
            if (Widgets.ButtonText(clearRect, "清空"))
            {
                ChatDisplayState.Clear();
                ToolDispatcher.ResetTaskCount();
            }

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
            if (streaming)
            {
                newH += 14f;
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

            if (Event.current.type == EventType.MouseDown
                && Event.current.button == 1
                && Mouse.IsOver(bubbleRect))
            {
                GUIUtility.systemCopyBuffer = entry.Text;
                Messages.Message("已复制到剪贴板", MessageTypeDefOf.SilentInput, false);
                Event.current.Use();
            }

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

            bool cursorOnThinking = isThinking && streaming;
            bool cursorOnBody = !isThinking && streaming;

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
