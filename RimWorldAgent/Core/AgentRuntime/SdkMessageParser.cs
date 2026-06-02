using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using RimWorldAgent.Core.CcbManager;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>
    /// SdkMessage → UiMessage 转换。
    /// 接收类型化的 SdkMessage 子类，不再碰 JSON。
    /// </summary>
    public static class SdkMessageParser
    {
        public static List<UiMessage> ParseToUiMessages(SdkMessage msg)
        {
            var result = new List<UiMessage>();
            try
            {
                switch (msg)
                {
                    case SdkAssistantMessage am:
                        ParseAssistant(am, result);
                        break;
                    case SdkStreamEventMessage se:
                        ParseStreamEvent(se, result);
                        break;
                    case SdkResultMessage rm:
                        result.Add(UiMessage.Result(rm.Subtype, rm.StopReason));
                        // 会话真实耗时（per-assistant 的 Record 调用中 durationMs 始终为 0）
                        TokenUsageTracker.Record(0, 0, 0, 0, rm.DurationMs ?? 0);
                        break;
                    case SdkSystemMessage sm:
                        if (sm.Subtype == "init")
                            result.Add(UiMessage.SystemInit(sm.Model, sm.SessionId,
                                sm.ClaudeCodeVersion, sm.PermissionMode,
                                sm.McpServers.Select(m => new UiMcpServerRef(m.Name, m.Status)).ToList(),
                                sm.Tools, sm.Skills));
                        break;
                    case SdkAbortedMessage _:
                        result.Add(UiMessage.Aborted());
                        break;
                    case SdkUserMessage um:
                        // SDK user 消息仅含 tool_result block（工具执行结果）
                        foreach (var block in um.Content)
                        {
                            if (block is SdkToolResultBlock tr)
                            {
                                result.Add(UiMessage.ToolResult(tr.ToolUseId ?? "", tr.IsError, 0, tr.Content));
                                UIMessageBus.RaiseToolResultRecorded(tr.ToolUseId ?? "", tr.IsError, tr.Content);
                            }
                        }
                        break;
                    default:
                        CoreLog.Info($"[SdkMessageParser] 未处理的类型: {msg.Type}");
                        break;
                }
            }
            catch (Exception ex) { CoreLog.Warn($"[SdkMessageParser] 解析失败: {ex.Message}"); }
            return result;
        }

        private static void ParseAssistant(SdkAssistantMessage msg, List<UiMessage> outList)
        {
            // Token 用量
            if (msg.Usage != null)
            {
                TokenUsageTracker.Record(msg.Usage.InputTokens, msg.Usage.OutputTokens,
                    msg.Usage.CacheReadInputTokens ?? 0, msg.Usage.CacheCreationInputTokens ?? 0, 0);
                TokenUsageTracker.CurrentInputTokens = msg.Usage.InputTokens;
            }

            // 积累 text + thinking 文本，用于会话录制
            var textAccum = new System.Text.StringBuilder();
            var thinkingAccum = new System.Text.StringBuilder();

            // 文本由 stream_event TextDelta 实时推送，assistant 只发 tool_call
            foreach (var block in msg.Content)
            {
                if (block is SdkTextBlock tb)
                {
                    textAccum.Append(tb.Text);
                }
                else if (block is SdkThinkingBlock th)
                {
                    thinkingAccum.Append(th.Thinking);
                }
                else if (block is SdkToolUseBlock tu)
                {
                    outList.Add(UiMessage.ToolCall(tu.Id, tu.Name, tu.Input));
                    UIMessageBus.RaiseToolCallRecorded(tu.Id, tu.Name, tu.Input);
                }
            }

            // 提取 runId 和 agentType 用于录制
            var runId = msg.Uuid ?? "";
            var agentType = msg.ParentToolUseId ?? "";

            var finalText = textAccum.ToString();
            var finalThinking = thinkingAccum.ToString();
            if (finalText.Length > 0 || finalThinking.Length > 0)
                UIMessageBus.RaiseAssistantContent(finalText, finalThinking, runId, agentType);
        }

        private static void ParseStreamEvent(SdkStreamEventMessage msg, List<UiMessage> outList)
        {
            var evt = msg.Event;
            if (evt == null) return;

            switch (evt.EventType)
            {
                case "content_block_start":
                    if (evt.BlockType == "text")
                        outList.Add(UiMessage.TextDelta(""));
                    else if (evt.BlockType == "thinking")
                        outList.Add(UiMessage.ThinkingDelta(""));
                    else if (evt.BlockType == "tool_use")
                        outList.Add(UiMessage.ToolCall(evt.ToolUseId ?? "", evt.ToolName ?? "", ""));
                    break;
                case "content_block_delta":
                    if (evt.Text != null)
                        outList.Add(UiMessage.TextDelta(evt.Text));
                    else if (evt.Thinking != null)
                        outList.Add(UiMessage.ThinkingDelta(evt.Thinking));
                    break;
            }
        }
    }
}
