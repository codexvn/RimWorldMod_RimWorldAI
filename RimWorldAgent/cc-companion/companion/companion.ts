#!/usr/bin/env tsx
/**
 * Claude Code SDK 桥接 — 纯 WS，只认 4 种消息
 *
 * WebSocket 协议：
 *   C# → companion:  {"type":"chat", "text":"...", "session":"bus|system", "thinking":{mode,effort,tokens?}}
 *                    {"type":"abort"[, "clear":true]}
 *   companion → C#:  {"type":"hello-ok"}
 *                    SDK 消息 (type: assistant / stream_event / result / system / user / aborted)
 */

import { writeFileSync, unlinkSync, appendFileSync, readFileSync, existsSync } from 'fs';
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

function formatErrorChain(err: unknown): string {
  if (!(err instanceof Error)) return String(err);
  const parts: string[] = [];
  let current: unknown = err;
  while (current instanceof Error) {
    parts.push(`${current.name}: ${current.message}`);
    current = current.cause;
  }
  if (current !== undefined) parts.push(String(current));
  return parts.join(' <- ');
}

function sendJson(ws: WebSocket, obj: Record<string, unknown>) {
  if (ws.readyState === 1) ws.send(JSON.stringify(obj));
}

let sdkLogPath: string | null = null;
if (CONFIG.logSdk) {
  sdkLogPath = join(CONFIG.projectPath, 'sdk-log.txt');
  log(`SDK 日志已启用: ${sdkLogPath}`);
}

function sdkLog(dir: '→' | '←', data: string) {
  if (!sdkLogPath) return;
  try {
    const now = new Date().toISOString();
    appendFileSync(sdkLogPath, `[${now}] ${dir} ${data}\n`, 'utf8');
  } catch (err) {
    log(`SDK 日志写入失败: ${err instanceof Error ? err.message : String(err)}`);
  }
}

// ===== session-id.txt 读写 =====

const sidFile = join(CONFIG.projectPath, 'session-id.txt');

function readSessionId(): string | undefined {
  try {
    if (existsSync(sidFile)) {
      const id = readFileSync(sidFile, 'utf8').trim();
      return id || undefined;
    }
  } catch (err) {
    log(`读取 session-id.txt 失败: ${err instanceof Error ? err.message : String(err)}`);
  }
  return undefined;
}

function writeSessionId(id: string) {
  try {
    const old = readSessionId();
    if (old === id) return; // 幂等：相同不重写
    writeFileSync(sidFile, id, 'utf8');
    log(`写 session-id.txt: ${id}`);
  } catch (err) {
    log(`写 session-id.txt 失败: ${err instanceof Error ? err.message : String(err)}`);
  }
}

function deleteSessionIdFile() {
  try {
    if (existsSync(sidFile)) {
      unlinkSync(sidFile);
      log(`删 session-id.txt`);
    }
  } catch (err) {
    log(`删 session-id.txt 失败: ${err instanceof Error ? err.message : String(err)}`);
  }
}

async function main() {
  log(`启动 PID=${process.pid} port=${CONFIG.port} model=${CONFIG.modelName || 'default'}`);

  const sdk = await loadClaudeSdk();
  let busBroadcast: (data: string) => void;

  // ===== SDK 会话 =====
  let abortController = new AbortController();
  let session = createSession(sdk, abortController);
  let { inputStream, queryIterator } = session;
  // abort→重建期间的缓冲队列
  let buffering = false;
  let pendingMessages: any[] = [];
  // stream 是否已 abort（防 chat 写入已关闭 stream）
  let streamAborted = false;
  let processorPromise: Promise<void> | null = null;
  let aborting: Promise<void> | null = null;
  // 每个 SDK processor 绑定 generation；abort/rebuild 后旧 generation 的迟到消息直接丢弃。
  let generation = 0;
  const abortWaitTimeoutMs = 2000;

  // 当前 SDK 会话 ID（从 system.init 捕获，用于 abort 后 resume）
  let currentSessionId: string | undefined = readSessionId();

  function startNewSession() {
    abortController = new AbortController();
    session = createSession(sdk, abortController);
    inputStream = session.inputStream;
    queryIterator = session.queryIterator;
    streamAborted = false;
    // 回放缓冲消息到新 stream
    if (pendingMessages.length > 0) {
      log(`回放缓冲消息 count=${pendingMessages.length}`);
      for (const m of pendingMessages) inputStream.enqueue(m);
      pendingMessages = [];
    }
    buffering = false;
    startProcessor();
    log(`新会话已创建${currentSessionId ? ' (resume=' + currentSessionId + ')' : ''}`);
  }

  function onSdkMessage(msg: any) {
    // 从 system.init 捕获会话 ID，写入 session-id.txt
    if (msg.type === 'system' && msg.subtype === 'init' && msg.session_id) {
      currentSessionId = msg.session_id;
      writeSessionId(msg.session_id);
      log(`会话 ID: ${currentSessionId}`);
    }
    busBroadcast(JSON.stringify(msg));
  }

  function startProcessor() {
    const processorGeneration = ++generation;
    const proc = createResponseProcessor(queryIterator, (msg) => {
      if (processorGeneration !== generation) return;
      setImmediate(() => {
        if (processorGeneration !== generation) return;
        onSdkMessage(msg);
      });
    });
    processorPromise = proc.process().catch((err: any) => {
      log(`SDK 处理异常: ${formatErrorChain(err)}`);
    });
  }

  async function waitForProcessorStop(processor: Promise<void> | null) {
    if (!processor) return;
    let timedOut = false;
    await Promise.race([
      processor.catch((err: any) => log(`等待 SDK 停止时异常: ${formatErrorChain(err)}`)),
      new Promise<void>((resolve) => setTimeout(() => {
        timedOut = true;
        resolve();
      }, abortWaitTimeoutMs)),
    ]);
    if (timedOut) log(`等待旧 SDK 会话停止超时: ${abortWaitTimeoutMs}ms`);
  }

  async function abortAndRestart(clear: boolean) {
    if (aborting) {
      await aborting;
      return;
    }

    aborting = (async () => {
      if (clear) {
        // clear=true 表示用户主动清空上下文：删除持久化 session id，下一轮不 resume 旧 SDK 会话。
        currentSessionId = undefined;
        deleteSessionIdFile();
      }

      const processorToStop = processorPromise;
      log(`收到 abort, buffering=true, resume=${currentSessionId || '(新会话)'}`);
      buffering = true;
      streamAborted = true;  // 旧 stream 不可写
      generation++;
      abortController.abort();
      log('abortController.abort() done, waiting old session...');
      await waitForProcessorStop(processorToStop);
      log('旧会话已停止, startNewSession...');
      startNewSession();
    })();

    try {
      await aborting;
    } finally {
      aborting = null;
    }
  }

  function applyThinking(cfg?: ThinkingConfig) {
    if (!cfg?.mode) return;
    if (cfg.mode === Thinking.mode && cfg.effort === Thinking.effort) return;
    Thinking.mode = cfg.mode;
    if (cfg.effort) Thinking.effort = cfg.effort;
    log(`思考模式: ${Thinking.mode}${cfg.effort ? ' effort=' + cfg.effort : ''}`);
    // abort 旧 session，防止新旧 processor 同时输出导致消息重复
    generation++;
    abortController.abort();
    buffering = true;
    startNewSession();
  }

  // ===== WS Server（先于 SDK 启动，避免竞态）=====
  const httpServer = createServer();
  const wss = new WebSocketServer({ server: httpServer });
  httpServer.listen(CONFIG.port, CONFIG.host);

  busBroadcast = (data: string) => {
    sdkLog('←', data);
    for (const c of wss.clients) {
      if (c.readyState === 1) c.send(data);
    }
  };

  wss.on('connection', (ws: WebSocket) => {
    log(`新 WS 连接, auth=${CONFIG.token ? 'required' : 'none'}`);
    let authenticated = !CONFIG.token;

    ws.on('message', async (data: Buffer) => {
      let raw: any;
      try { raw = JSON.parse(data.toString().trim()); }
      catch (err) {
        log(`无效 JSON: ${formatErrorChain(err)}`);
        return;
      }
      const msg = raw as any;
      log(`收到消息 type=${msg.type} auth=${msg.auth?.token ? 'provided' : 'none'}`);

      // auth
      if (msg.type === 'hello') {
        if (!authenticated) {
          if (msg.auth?.token === CONFIG.token) {
            authenticated = true;
          } else {
            sendJson(ws, { type: 'error', error: 'auth failed' });
            log(`auth 失败: provided=${msg.auth?.token ? 'yes' : 'no'} expected=${CONFIG.token ? 'yes' : 'no'}`);
            ws.close();
            return;
          }
        }
        sendJson(ws, { type: 'hello-ok' });
        log(`hello-ok 已发送${!CONFIG.token ? ' (无认证)' : ''}`);
        return;
      }
      if (!authenticated) return;

      // dispatch
      switch (msg.type) {
        case 'chat': {
          applyThinking(msg.thinking);
          log(`chat session=${msg.session} len=${msg.text.length} buffering=${buffering} streamAborted=${streamAborted}`);
          const userMsg = { type: 'user', message: { role: 'user', content: msg.text } };
          sdkLog('→', JSON.stringify(userMsg));
          if (buffering) {
            log(`chat 缓冲中...`);
            pendingMessages.push(userMsg);
          } else {
            inputStream.enqueue(userMsg);
          }
          break;
        }
        case 'abort':
          try {
            await abortAndRestart(Boolean(msg.clear));
            sendJson(ws, { type: 'aborted' });
          } catch (err) {
            log(`abort 处理失败: ${formatErrorChain(err)}`);
            sendJson(ws, { type: 'error', error: 'abort failed' });
          }
          break;
      }
    });
  });

  // WS server 就绪后启动 SDK 消息处理
  startProcessor();

  // ===== PID 文件 + 清理 =====
  const pidFile = join(process.cwd(), '.pid');
  writeFileSync(pidFile, String(process.pid));

  function shutdown() {
    try { unlinkSync(pidFile); }
    catch (err) { log(`删除 PID 文件失败: ${err instanceof Error ? err.message : String(err)}`); }
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
