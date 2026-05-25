/**
 * RimWorld 游戏上下文 — 系统提示词 + 事件→用户消息转换
 */

export function buildSystemPrompt(mcpUrl: string): string {
  return `

## RimWorld 游戏监控

你正在监控 RimWorld 游戏。你可以通过 MCP tool 查看游戏状态和控制游戏。

### 游戏事件处理规则

- 收到袭击通知时: 暂停游戏 → 调用 get_defense_status → 征召殖民者 → 指挥防御
- 收到死亡通知时: 检查殖民地状态 → 评估影响 → 给出建议
- 收到每日早报时: 做全面殖民地检查（资源/心情/威胁/研究）
- 收到负面事件时: 评估严重程度 → 决定是否需要人工介入
- 殖民者空闲时: 检查是否有待完成的工作 → 调整工作优先级

### MCP Tool 使用规则

- 所有写操作（建造/征召/标记）默认执行，不需要用户确认
- 操作前用 get_tile_detail 确认目标位置
- 坐标规则: pos_x=水平网格东西轴, pos_y=垂直网格南北轴（会被映射为 IntVec3.z）
- 涉及 Pawn 的操作使用 thingIDNumber 而非名称
- 区域操作使用左上角(pos) → 右下角(end)模式
- 写操作入队后等待 1-2 秒让游戏执行

### 当前 MCP Server

${mcpUrl}
`;
}

const ICONS: Record<string, string> = {
  RaidStart: '⚠️ [紧急]',
  RaidEnd: '✅',
  PawnDeath: '💀 [紧急]',
  NegativeEvent: '⚠️',
  AlertStart: '⚠️',
  DailyMorning: '🌅',
  IdleDetected: '⏳',
};

const INSTRUCTIONS: Record<string, string> = {
  RaidStart: '\n请立即评估威胁并指挥防御。',
  PawnDeath: '\n请检查殖民地状态并评估影响。',
  DailyMorning: '\n请做全面的殖民地检查。',
  NegativeEvent: '\n请评估严重程度并给出应对建议。',
  AlertStart: '\n请检查并处理此警报。',
  IdleDetected: '\n请检查是否有待分配的工作。',
};

export interface GameEvent {
  event?: string;
  payload?: {
    category?: string;
    text?: string;
    severity?: string;
    timestamp?: number;
  };
}

export function gameEventToText(event: GameEvent): string {
  const payload = event.payload || {};
  const text = payload.text || '';
  const category = payload.category || event.event || '';
  const icon = ICONS[category] || '📢';
  const suffix = INSTRUCTIONS[category] || '';
  return `${icon} ${text}${suffix}`;
}
