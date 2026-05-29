/**
 * 统一消息总线
 *
 * ## 背景
 *
 * Companion 通过一条 WebSocket 承载两类消息流（Bus），两股流共享同一个 server.broadcast() 但来源/语义不同：
 *
 *   Game Bus（游戏事件流）
 *     C# → WS → Companion onEvent → 直接 broadcast（不经 SDK）
 *     内容：殖民地统计、TODO、预算状态、用户发言回显、错误通知
 *     消费者：Web 聊天页面、游戏内 UI（C# 侧自有数据源，不走 WS 回传）
 *
 *   Agent Bus（AI 对话流）
 *     SDK query() → processResponses → broadcast
 *     内容：assistant 文本/思考、stream_event 增量、tool_use/tool_result、result、system/init
 *     消费者：Web 聊天页面、游戏内 UI（C# CCClient.ReceiveLoop → ChatDisplayState）
 *
 * ## 职责
 *
 * - 集中定义所有 WS 广播消息的类型签名，避免散落的 JSON.stringify({ type: '...', ... })
 * - 提供语义化发布方法，调用方不关心序列化细节
 * - 新增消息类型时只需在此文件加一个方法，不会遗漏类型约束
 *
 * ## 与 ws-server.ts 中 broadcast 的关系
 *
 * ws-server 内部的 broadcast() 用于协议层握手（hello-ok / pong / aborted 确认），
 * 以及认证失败时的 error。这些是 WebSocket 协议级别消息，不归 MessageBus 管。
 * MessageBus 管的是"业务消息"——游戏事件和 AI 对话。
 */

// ===== 类型定义 =====

/** 殖民地统计数据载荷（C# BridgeLifecycle.BuildColonyStats 产出） */
export interface ColonyStatsPayload {
  /** 殖民者总数 */
  colonistCount: number;
  /** 平均心情百分比 (0-100) */
  avgMood: number;
  /** 食物可支撑天数 */
  foodDays: number;
  /** 殖民地名称（Find.World.info.name） */
  colonyName: string;
}

/** 底层广播函数签名：接收已序列化的 JSON 字符串，发送给所有 WebSocket 客户端 */
export type BroadcastFn = (data: string) => void;

// ===== MessageBus =====

export class MessageBus {
  private broadcast: BroadcastFn;

  /**
   * @param broadcast — 底层广播函数，通常来自 ws-server 的 server.broadcast
   */
  constructor(broadcast: BroadcastFn) {
    this.broadcast = broadcast;
  }

  // ==================== Game Bus ====================

  /**
   * 广播殖民地实时统计（人口 / 心情 / 食物）。
   *
   * 触发时机：C# 发送每日早报或空闲兜底时，payload 中携带 colonyStats。
   * 消费者：Web 页面左侧栏 Pawns/Mood/Food 卡片。
   * 路径：C# BridgeLifecycle.SendCCMessage → CCClient.SendEventText → WS event
   *       → companion onEvent → bus.publishColonyStats
   */
  publishColonyStats(stats: ColonyStatsPayload): void {
    this.send({ type: 'colony-stats', ...stats });
  }

  /**
   * 广播 TODO 列表。
   *
   * 触发时机：C# 推送 todo-state 事件（新增/完成/删除 TODO 时）。
   * 消费者：Web 页面右侧栏 TODO 卡片。
   * 注意：纯 UI 消息，不入队到 SDK（不消耗 Token）。
   */
  publishTodoState(items: Array<Record<string, unknown>>): void {
    this.send({ type: 'todo-state', todoItems: items });
  }

  /**
   * 广播 Token 预算状态。
   *
   * 触发时机：C# hello 握手时附带初始预算；后续每次 SDK result 有 usage 时也更新。
   * 消费者：Web 页面 header budget 条（绿/黄/红进度）。
   */
  publishBudgetStatus(limit: number, used: number, action: string): void {
    this.send({ type: 'budget-status', limit, used, action });
  }

  /**
   * 回显用户发言到 UI（不经 SDK，零延迟）。
   *
   * 为什么需要：SDK 的 query() 是异步流，用户消息要等 SDK echo 回来才有 "USER" 气泡。
   * 这里直接广播，确保 Web 页面在 SDK 处理前就能立刻看到用户说了什么。
   * SDK 后续 echo 的 user 消息走 Agent Bus，Web 端会去重。
   */
  publishUserMessage(text: string): void {
    this.send({
      type: 'user',
      message: { role: 'user', content: text },
    });
  }

  /**
   * 广播当前活跃的 Agent 角色（Overseer/Economy/Combat/Medic/休眠中）。
   *
   * 触发时机：Agent Runtime 唤醒/休眠 Agent 时。
   * 消费者：Web 页面 header agent-role 显示。
   */
  publishAgentStatus(role: string): void {
    this.send({ type: 'agent-status', role });
  }

  /**
   * 广播 Companion 级别的错误通知（非 SDK 错误）。
   *
   * 当前场景：Token 预算超额且为 Block 模式时，阻止消息并通知所有客户端。
   */
  publishError(err: string): void {
    this.send({ type: 'error', error: err });
  }

  /**
   * 广播 SDK 解析后的模型名。
   *
   * 触发时机：SDK system/init 消息到达时（onInit 回调）。
   * 与 system/init 分开广播：init 体量大（tools/mcp list），C# 侧只消费 model 字段。
   */
  publishModelInfo(model: string): void {
    this.send({ type: 'model-info', model });
  }

  // ==================== Agent Bus ====================

  /**
   * 代理 SDK 返回的所有消息到 UI 客户端。
   *
   * 包含的消息类型：
   * - system/init   — SDK 初始化完成（model、version、session_id、mcp_servers、tools）
   * - assistant     — AI 回复（文本/思考/tool_use/tool_result content blocks）
   * - user          — SDK echo 的用户消息
   * - stream_event  — 流式增量（content_block_start/delta，含 thinking_delta、text_delta）
   * - result        — 回合结束（subtype: success/aborted/error，含 usage 数据）
   *
   * 这些消息由 SDK query() 的 AsyncIterator 产出，经 createResponseProcessor 遍历后逐条传入。
   * 消费者：Web 页面（渲染对话）、游戏内 C# CCClient.ReceiveLoop（ChatDisplayState）。
   */
  publishSdkMessage(msg: Record<string, unknown>): void {
    this.send(msg);
  }

  /**
   * 广播中断确认。
   *
   * 触发时机：C# 发送 abort → ws-server onAbort 回调。
   * 消费者：Web 页面（标记最后一个流式条目为 "已中断"），C#（Tool_AdvanceTick.CancelAll）。
   */
  publishAborted(): void {
    this.send({ type: 'aborted' });
  }

  // ==================== 内部 ====================

  /** 序列化并广播 */
  private send(msg: Record<string, unknown>): void {
    this.broadcast(JSON.stringify(msg));
  }
}
