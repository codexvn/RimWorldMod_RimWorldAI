#!/usr/bin/env tsx
/**
 * Claude Code SDK 桥接 — 纯 WS，只认 4 种消息
 *
 * WebSocket 协议：
 *   C# → companion:  {"type":"chat", "text":"...", "session":"bus|system", "thinking":{mode,effort,tokens?}}
 *                    {"type":"abort"}
 *   companion → C#:  {"type":"hello-ok"}
 *                    SDK 消息 (type: assistant / stream_event / result / system / user / aborted)
 */

import { writeFileSync, unlinkSync } from 'fs';
import { join } from 'path';
import { createServer } from 'http';
import { WebSocketServer, WebSocket } from 'ws';
import { CONFIG, Thinking, parseArgs } from './config.js';
import { loadClaudeSdk } from './sdk-loader.js';
import { createSession, createResponseProcessor } from '../bridge/session.js';
import type { InboundMessage, ThinkingConfig } from './protocol.js';

parseArgs(process.argv);

function log(text: string) {
  console.log(`[bridge] ${text}`);
}

function sendJson(ws: WebSocket, obj: Record<string, unknown>) {
  if (ws.readyState === 1) ws.send(JSON.stringify(obj));
}

async function main() {
  log(`启动 PID=${process.pid} port=${CONFIG.port} model=${CONFIG.modelName || 'default'}`);

  const sdk = await loadClaudeSdk();
  let busBroadcast: (data: string) => void;

  // ===== SDK 会话 =====
  let abortController = new AbortController();
  let session = createSession(sdk, abortController);
  let { inputStream, queryIterator } = session;

  function startNewSession() {
    abortController = new AbortController();
    session = createSession(sdk, abortController);
    inputStream = session.inputStream;
    queryIterator = session.queryIterator;
    const proc = createResponseProcessor(queryIterator, (msg) => setImmediate(() => busBroadcast(JSON.stringify(msg))));
    proc.process();
    log('新会话已创建');
  }

  function applyThinking(cfg?: ThinkingConfig) {
    if (!cfg?.mode) return;
    if (cfg.mode === Thinking.mode && cfg.effort === Thinking.effort && cfg.tokens === Thinking.maxTokens) return;
    Thinking.mode = cfg.mode;
    if (cfg.effort) Thinking.effort = cfg.effort;
    if (cfg.tokens != null) Thinking.maxTokens = cfg.tokens;
    log(`思考模式: ${Thinking.mode}${cfg.effort ? ' effort=' + cfg.effort : ''}`);
    startNewSession();
  }

  // ===== WS Server（先于 SDK 启动，避免竞态）=====
  const httpServer = createServer();
  const wss = new WebSocketServer({ server: httpServer });
  httpServer.listen(CONFIG.port, CONFIG.host);

  busBroadcast = (data: string) => {
    for (const c of wss.clients) {
      if (c.readyState === 1) c.send(data);
    }
  };

  wss.on('connection', (ws: WebSocket) => {
    log(`[CCGUI_DEBUG] 新 WS 连接, token=${CONFIG.token ? 'required' : 'none'}`);
    let authenticated = !CONFIG.token;

    ws.on('message', (data: Buffer) => {
      let raw: any;
      try { raw = JSON.parse(data.toString().trim()); }
      catch {
        log(`[CCGUI_DEBUG] 无效 JSON: ${data.toString().substring(0, 200)}`);
        return;
      }
      const msg = raw as any;
      log(`[CCGUI_DEBUG] 收到消息 type=${msg.type} token=${msg.auth?.token || '(none)'}`);

      // auth
      if (msg.type === 'hello') {
        if (!authenticated) {
          if (msg.auth?.token === CONFIG.token) {
            authenticated = true;
          } else {
            sendJson(ws, { type: 'error', error: 'auth failed' });
            log(`[CCGUI_DEBUG] auth 失败: msg.token='${msg.auth?.token}' config.token='${CONFIG.token}'`);
            ws.close();
            return;
          }
        }
        sendJson(ws, { type: 'hello-ok' });
        log(`[CCGUI_DEBUG] hello-ok 已发送${!CONFIG.token ? ' (无认证)' : ''}`);
        return;
      }
      if (!authenticated) return;

      // dispatch
      switch (msg.type) {
        case 'chat': {
          applyThinking(msg.thinking);
          log(`chat session=${msg.session} len=${msg.text.length}`);
          inputStream.enqueue({ type: 'user', message: { role: 'user', content: msg.text } });
          break;
        }
        case 'abort':
          log('收到 abort');
          inputStream.done();
          queryIterator.return?.();
          startNewSession();
          break;
      }
    });
  });

  // WS server 就绪后启动 SDK 消息处理
  const proc = createResponseProcessor(queryIterator, (msg) => setImmediate(() => busBroadcast(JSON.stringify(msg))));
  proc.process().catch((err: any) => log(`SDK 处理异常: ${err.message}`));

  // ===== PID 文件 + 清理 =====
  const pidFile = join(process.cwd(), '.pid');
  writeFileSync(pidFile, String(process.pid));

  function shutdown() {
    try { unlinkSync(pidFile); } catch {}
    process.exit(0);
  }
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

  log(`就绪 ws://${CONFIG.host}:${CONFIG.port}`);
}

main().catch((err: any) => {
  console.error(`[bridge] 致命错误: ${err.message}`);
  process.exit(1);
});
