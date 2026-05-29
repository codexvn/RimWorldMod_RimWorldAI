/**
 * HTTP 路由 — 提供聊天页面 + 历史消息 API（与 WebSocket 共享同一端口）
 */

import type { Server, IncomingMessage, ServerResponse } from 'http';
import { readFileSync, existsSync } from 'fs';
import { RuntimeState } from '../companion/config.js';

/** 获取最后 N 行的通用函数 */
function readLastLines(path: string, n: number): string[] {
  const data = readFileSync(path, 'utf8');
  const lines = data.split('\n').filter(line => line.trim());
  return lines.slice(-n);
}

export function setupChatHttp(httpServer: Server, chatPageHtml: string, projectPath: string): void {
  httpServer.on('request', (req: IncomingMessage, res: ServerResponse) => {
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

    if (req.method === 'GET' && (url.pathname === '/' || url.pathname === '/chat')) {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(chatPageHtml);
      return;
    }

    if (req.method === 'GET' && url.pathname === '/history') {
      try {
        const n = parseInt(url.searchParams.get('n') || '30');
        const sessionFile = RuntimeState.sessionFilePath;
        if (!sessionFile || !existsSync(sessionFile)) {
          res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
          res.end('[]');
          return;
        }
        const lines = readLastLines(sessionFile, Math.min(n, 100));
        const messages = lines.map(line => {
          try { return JSON.parse(line); } catch { return null; }
        }).filter(Boolean);
        res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify(messages));
      } catch (err: any) {
        res.writeHead(500, { 'Content-Type': 'application/json; charset=utf-8' });
        res.end(JSON.stringify({ error: err.message }));
      }
      return;
    }

    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('404 Not Found');
  });
}
