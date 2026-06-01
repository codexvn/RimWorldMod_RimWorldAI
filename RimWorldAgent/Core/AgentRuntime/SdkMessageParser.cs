using System;
using System.Collections.Generic;
using RimWorldAgent.Core.CcbManager;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>
    /// SdkMessage → UiMessage 转换。
    /// 接收类型化的 SdkMessage 子类，不再碰 JSON。
    /// </summary>
    public static class SdkMessageParser
    {
        public static List<string> ParseToUiMessages(SdkMessage msg)
        {
            var result = new List<string>();
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
                        break;
                    case SdkSystemMessage sm:
                        if (sm.Subtype == "init")
                            result.Add(UiMessage.SystemInit(sm.Model, sm.SessionId));
                        break;
                    case SdkAbortedMessage _:
                        result.Add(UiMessage.Aborted());
                        break;
                    case SdkUserMessage um:
                        // user 消息不作 UiMessage 转换，UI 通过其他信道获取
                        break;
                    default:
                        CoreLog.Info($"[SdkMessageParser] 未处理的类型: {msg.Type}");
                        break;
                }
            }
            catch (Exception ex) { CoreLog.Warn($"[SdkMessageParser] 解析失败: {ex.Message}"); }
            return result;
        }

        private static void ParseAssistant(SdkAssistantMessage msg, List<string> outList)
        {
            // Token 用量
            if (msg.Usage != null)
                TokenUsageTracker.Record(msg.Usage.InputTokens, msg.Usage.OutputTokens,
                    msg.Usage.CacheReadInputTokens ?? 0, msg.Usage.CacheCreationInputTokens ?? 0, 0);

            foreach (var block in msg.Content)
            {
                if (block is SdkTextBlock tb)
                    outList.Add(UiMessage.TextBlock(tb.Text));
                else if (block is SdkToolUseBlock tu)
                    outList.Add(UiMessage.ToolCall(tu.Id, tu.Name, tu.Input));
            }
        }

        private static void ParseStreamEvent(SdkStreamEventMessage msg, List<string> outList)
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
