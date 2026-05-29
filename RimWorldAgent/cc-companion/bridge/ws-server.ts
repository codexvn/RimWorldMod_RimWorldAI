/**
 * WebSocket Server — 接收 RimWorldMCP CCClient 连接，接收游戏事件
 */

import { WebSocketServer, WebSocket } from 'ws';
import { createServer } from 'http';
import type { IncomingMessage } from 'http';
import { CONFIG, RuntimeState } from '../companion/config.js';

export interface WsMessage {
  type: string;
  event?: string;
  payload?: Record<string, unknown>;
  auth?: { token?: string };
  client?: { name?: string; version?: string };
  budget?: { limit: number; used: number; action: string };
  colonyStats?: Record<string, unknown>;
}

export interface StatusChange {
  status: 'listening' | 'connected' | 'disconnected' | 'error';
  port?: number;
  client?: { name?: string; version?: string } | string;
  error?: string;
}

type EventCallback = (msg: WsMessage) => void;
type StatusCallback = (status: StatusChange) => void;
type AbortCallback = () => void;
type SetThinkingCallback = (mode: string, effort?: string, tokens?: number) => void;

export function createWSServer(
  port: number,
  host: string,
  token: string,
  onEvent: EventCallback,
  onStatusChange: StatusCallback,
  onAbort?: AbortCallback,
  onSetThinking?: SetThinkingCallback,
) {
  const httpServer = createServer();
  httpServer.on('error', (err: Error) => {
    console.error(`[cc-companion] HTTP 服务器错误: ${err.message}`);
  });
  httpServer.on('listening', () => {
    // 设置 SO_REUSEADDR 允许 TIME_WAIT 端口立即重用
    const addr = httpServer.address();
    if (addr && typeof addr === 'object') {
      console.log(`[cc-companion] WebSocket 服务器已启动: ws://${addr.address}:${addr.port}`);
    }
    onStatusChange?.({ status: 'listening', port });
  });

  const wss = new WebSocketServer({ server: httpServer });
  httpServer.listen(port, host);

  wss.on('error', (err: Error) => {
    console.error(`[cc-companion] WebSocket 服务器错误: ${err.message}`);
    onStatusChange?.({ status: 'error', error: err.message });
  });

  // 广播给所有已认证客户端
  function broadcast(data: string): void {
    for (const c of wss.clients) {
      if (c.readyState === WebSocket.OPEN) c.send(data);
    }
  }

  wss.on('connection', (ws: WebSocket, req: IncomingMessage) => {
    const clientIp = req.socket.remoteAddress;
    console.log(`[cc-companion] 新连接: ${clientIp}`);

    let authenticated = token ? false : true;

    ws.on('message', (data: Buffer) => {
      try {
        const msg = JSON.parse(data.toString()) as WsMessage;
        handleMessage(ws, msg);
      } catch {
        console.warn(`[cc-companion] 无效 JSON: ${data.toString().substring(0, 200)}`);
        sendJson(ws, { type: 'error', error: 'invalid json' });
      }
    });

    ws.on('close', (code: number, reason: Buffer) => {
      console.log(`[cc-companion] 连接断开: ${clientIp} (code=${code}, reason=${reason?.toString() || 'none'})`);
      onStatusChange?.({ status: 'disconnected', client: clientIp });
    });

    ws.on('error', (err: Error) => {
      console.warn(`[cc-companion] 连接错误: ${clientIp}: ${err.message}`);
    });

    function handleMessage(ws: WebSocket, msg: WsMessage) {
      switch (msg.type) {
        case 'hello':
          if (token && msg.auth?.token !== token) {
            sendJson(ws, { type: 'error', error: 'auth failed: invalid token' });
            console.warn(`[cc-companion] 认证失败: ${clientIp}`);
            ws.close();
            return;
          }
          authenticated = true;
          // 解析 Token 预算
          if (msg.budget) {
            RuntimeState.tokenBudgetLimit = msg.budget.limit || 0;
            RuntimeState.tokenBudgetUsed = msg.budget.used || 0;
            RuntimeState.tokenBudgetAction = msg.budget.action || 'Block';
            console.log(`[cc-companion] Token 预算: ${RuntimeState.tokenBudgetUsed}/${RuntimeState.tokenBudgetLimit} (${RuntimeState.tokenBudgetAction})`);
          }
          // 解析殖民地统计（来自 C# hello，填充 Web 侧边栏初始数据）
          if (msg.colonyStats) {
            RuntimeState.lastColonyStats = msg.colonyStats as Record<string, unknown>;
          }
          // 解析思考模式（来自 Mod 设置，通过 hello 传递）
          const thinking = (msg as any).thinking;
          if (thinking?.mode && thinking.mode !== 'Default') {
            RuntimeState.thinkingMode = thinking.mode.toLowerCase();
            if (thinking.effort) RuntimeState.thinkingEffort = thinking.effort;
            if (thinking.tokens) RuntimeState.maxThinkingTokens = thinking.tokens;
            console.log(`[cc-companion] 思考模式(Mod): ${RuntimeState.thinkingMode}${thinking.effort ? ' effort=' + thinking.effort : ''}${thinking.tokens ? ' tokens=' + thinking.tokens : ''}`);
          }
          console.log(`[cc-companion] 握手完成: ${msg.client?.name || 'unknown'} v${msg.client?.version || '?'}`);
          sendJson(ws, { type: 'hello-ok' });
          // 广播 Token 预算状态给聊天页面
          broadcast(JSON.stringify({
            type: 'budget-status',
            limit: RuntimeState.tokenBudgetLimit,
            used: RuntimeState.tokenBudgetUsed,
            action: RuntimeState.tokenBudgetAction,
          }));
          // 推送缓存的 SDK init 数据给新客户端（Web 页面）
          if (RuntimeState.lastInitData) {
            sendJson(ws, RuntimeState.lastInitData);
          }
          // 推送缓存的游戏状态（新客户端立即显示）
          if (RuntimeState.lastColonyStats) {
            sendJson(ws, { type: 'colony-stats', ...RuntimeState.lastColonyStats });
          }
          if (RuntimeState.lastTodoItems) {
            sendJson(ws, { type: 'todo-state', todoItems: RuntimeState.lastTodoItems });
          }
          if (RuntimeState.sdkTasks.length > 0) {
            sendJson(ws, { type: 'sdk-tasks', tasks: RuntimeState.sdkTasks });
          }
          onStatusChange?.({ status: 'connected', client: msg.client });
          break;

        case 'event':
          if (!authenticated) {
            sendJson(ws, { type: 'error', error: 'not authenticated' });
            return;
          }
          onEvent?.(msg);
          break;

        case 'ping':
          sendJson(ws, { type: 'pong' });
          break;

        case 'abort':
          if (!authenticated) {
            sendJson(ws, { type: 'error', error: 'not authenticated' });
            return;
          }
          console.log('[cc-companion] 收到中断请求');
          onAbort?.();
          sendJson(ws, { type: 'aborted' });
          break;

        case 'set-thinking':
          if (!authenticated) { sendJson(ws, { type: 'error', error: 'not authenticated' }); return; }
          {
            const mode = (msg as any).mode || 'default';
            const effort = (msg as any).effort;
            const tokens = (msg as any).tokens;
            console.log(`[cc-companion] 思考模式切换: ${mode}${effort ? ' effort=' + effort : ''}${tokens ? ' tokens=' + tokens : ''}`);
            onSetThinking?.(mode, effort, tokens);
          }
          break;

        default:
          console.debug(`[cc-companion] 未知消息类型: ${msg.type}`);
      }
    }
  });

  return {
    broadcast(data: string) {
      const msg = typeof data === 'string' ? data : JSON.stringify(data);
      for (const c of wss.clients) {
        if (c.readyState === 1) c.send(msg);
      }
    },
    httpServer,
  };
}

function sendJson(ws: WebSocket, obj: Record<string, unknown>): void {
  if (ws.readyState !== 1) return;
  ws.send(JSON.stringify(obj));
}
