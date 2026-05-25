/**
 * WebSocket Server — 接收 RimWorldMCP CCClient 连接，接收游戏事件
 */

import { WebSocketServer, WebSocket } from 'ws';
import type { IncomingMessage } from 'http';

export interface WsMessage {
  type: string;
  event?: string;
  payload?: Record<string, unknown>;
  auth?: { token?: string };
  client?: { name?: string; version?: string };
}

export interface StatusChange {
  status: 'listening' | 'connected' | 'disconnected' | 'error';
  port?: number;
  client?: { name?: string; version?: string } | string;
  error?: string;
}

type EventCallback = (msg: WsMessage) => void;
type StatusCallback = (status: StatusChange) => void;

export function createWSServer(
  port: number,
  host: string,
  token: string,
  onEvent: EventCallback,
  onStatusChange: StatusCallback,
) {
  const wss = new WebSocketServer({ port, host });

  wss.on('listening', () => {
    console.log(`[cc-companion] WebSocket 服务器已启动: ws://${host}:${port}`);
    onStatusChange?.({ status: 'listening', port });
  });

  wss.on('error', (err: Error) => {
    console.error(`[cc-companion] WebSocket 服务器错误: ${err.message}`);
    onStatusChange?.({ status: 'error', error: err.message });
  });

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
          console.log(`[cc-companion] 握手完成: ${msg.client?.name || 'unknown'} v${msg.client?.version || '?'}`);
          sendJson(ws, { type: 'hello-ok' });
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

        default:
          console.debug(`[cc-companion] 未知消息类型: ${msg.type}`);
      }
    }
  });

  return {
    close() {
      console.log('[cc-companion] WebSocket 服务器关闭中...');
      wss.close();
    },
    getClients() {
      return [...wss.clients].filter((c: WebSocket) => c.readyState === 1);
    },
  };
}

function sendJson(ws: WebSocket, obj: Record<string, unknown>): void {
  if (ws.readyState !== 1) return;
  ws.send(JSON.stringify(obj));
}
