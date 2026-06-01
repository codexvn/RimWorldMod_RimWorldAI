namespace RimWorldAgent.Core.models;

/// <summary>
/// 聊天频道常量。C# 与 TS companion protocol.ts ChatMessage.session 对齐。
/// </summary>
public static class ChatChannel
{
    /// <summary>UIMessageBus 转发的用户消息</summary>
    public const string Bus = "bus";
    /// <summary>AgentLoop 系统 prompt（RunSessionAsync）</summary>
    public const string System = "system";
}
