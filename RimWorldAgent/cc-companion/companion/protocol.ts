/** 聊天频道常量，C# ChatChannel 对齐 */
export const ChatChannel = {
  Bus: 'bus',         // UIMessageBus 转发的用户消息
  System: 'system',   // AgentLoop 系统 prompt
} as const;

/** C# → companion WS 消息类型 */

export interface HelloMessage {
  type: 'hello';
  auth?: { token?: string };
  thinking?: ThinkingConfig;
}

export interface ChatMessage {
  type: 'chat';
  text: string;
  session: string;
  thinking?: ThinkingConfig;
}

export interface AbortMessage {
  type: 'abort';
}

export type InboundMessage = HelloMessage | ChatMessage | AbortMessage;

/** companion → C# WS 消息类型 */

export interface HelloOk {
  type: 'hello-ok';
}

export interface ErrorMessage {
  type: 'error';
  error: string;
}

export type OutboundMessage = HelloOk | ErrorMessage;

/** 思考配置（随 chat 一起发送，变更加 session） */

export interface ThinkingConfig {
  mode: 'default' | 'disabled' | 'adaptive' | 'fixed';
  effort?: 'low' | 'medium' | 'high' | 'xhigh';
  tokens?: number;
}
