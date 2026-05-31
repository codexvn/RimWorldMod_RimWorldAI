#!/usr/bin/env tsx
/**
 * Claude Code — RimWorldMCP 游戏 AI 助手
 *
 * 用法: tsx companion.ts [--port 19999] [--token xxx] [--model sonnet]
 * 详见 README.md
 */

import { writeFileSync, unlinkSync, existsSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { CONFIG, RuntimeState, parseArgs } from './config.js';
import { loadClaudeSdk } from './sdk-loader.js';
import { createWSServer } from '../bridge/ws-server.js';
import { createSession, createResponseProcessor } from '../bridge/session.js';
import { getChatPageHtml } from '../chat/chat-page.js';
import { setupChatHttp } from '../chat/chat-http.js';
import { MessageBus } from '../bridge/message-bus.js';

parseArgs(process.argv);

async function main(): Promise<void> {
  console.log('='.repeat(60));
  console.log('Claude Code — RimWorldMCP 游戏 AI 助手');
  console.log('='.repeat(60));
  console.log(`CWD: ${process.cwd()}`);
  console.log(`ARGV: ${process.argv.slice(2).join(' ')}`);

  console.log(`配置: host=${CONFIG.host} port=${CONFIG.port}`);
  console.log(`模型: ${CONFIG.modelName}`);
  console.log(`数据目录: ${CONFIG.projectPath}`);
  console.log('');

  // 1. 写出 project/local setting sources（C# 传入的 MCP 权限模版等）
  if (CONFIG.projectSettingSources) {
    writeSettingFile(join(CONFIG.projectPath, '.claude', 'settings.json'), CONFIG.projectSettingSources);
  }
  if (CONFIG.localSettingSources) {
    writeSettingFile(join(CONFIG.projectPath, '.claude', 'settings.local.json'), CONFIG.localSettingSources);
  }

  // 2. 生成聊天页面 HTML
  const chatPageHtml = CONFIG.chatPageEnabled ? getChatPageHtml({
    token: CONFIG.token,
    modelName: CONFIG.modelName,
    projectPath: CONFIG.projectPath,
  }) : '';

  // 3. 加载 SDK
  console.log('[cc-companion] 加载 Claude Agent SDK...');
  let sdk: any;
  try {
    sdk = await loadClaudeSdk();
  } catch (err: any) {
    console.error(`[cc-companion] SDK 加载失败: ${err.message}`);
    process.exit(1);
  }

  // 4. 启动 WebSocket 服务器（先于 session，broadcast 需要引用）
  console.log('[cc-companion] 启动 WebSocket 服务器...');

  let lastInitSnapshot = '';
  const onInit = (msg: any) => {
    const model = msg.model || '?';
    const sessionId = msg.session_id || '?';
    const mcpStatus = msg.mcp_servers?.length
      ? msg.mcp_servers.map((s: any) => `${s.name}(${s.status})`).join(',')
      : '无';
    const snapshot = `${model}|${sessionId}|${mcpStatus}`;

    if (!lastInitSnapshot) {
      lastInitSnapshot = snapshot;
      console.log(`[cc-companion] 模型=${model} 会话=${sessionId} MCP=[${mcpStatus}]`);
    } else if (snapshot !== lastInitSnapshot) {
      console.log(`[cc-companion] Init 变更: 模型=${model} 会话=${sessionId}`);
      lastInitSnapshot = snapshot;
    }

    if (msg.model) {
      RuntimeState.resolvedModel = msg.model;
      bus?.publishModelInfo(msg.model);
    }
    RuntimeState.lastInitData = msg;
  };

  let abortController = new AbortController();
  let { inputStream, queryIterator } = createSession(sdk, CONFIG, abortController);

  let bus: MessageBus;

  let currentProc = createResponseProcessor(
    queryIterator,
    CONFIG.projectPath,
    // Agent Bus：SDK 所有消息 → Web + C# 客户端（setImmediate 避免阻塞 SDK 迭代器）
    (msg) => setImmediate(() => bus.publishSdkMessage(msg as Record<string, unknown>)),
    onInit,
  );
  let processResponses = currentProc.process;

  function applyThinking(mode: string, effort?: string, tokens?: number) {
    RuntimeState.thinkingMode = mode;
    if (effort) RuntimeState.thinkingEffort = effort;
    if (tokens) RuntimeState.maxThinkingTokens = tokens;

    if (mode === 'disabled') {
      queryIterator.setMaxThinkingTokens?.(0);
    } else if (mode === 'fixed') {
      queryIterator.setMaxThinkingTokens?.(tokens || 8000);
    } else {
      startNewSession();
    }
  }

  function startNewSession() {
    abortController = new AbortController();
    const result = createSession(sdk, CONFIG, abortController);
    inputStream = result.inputStream;
    queryIterator = result.queryIterator;
    currentProc = createResponseProcessor(
      queryIterator,
      CONFIG.projectPath,
      // Agent Bus：SDK 所有消息 → Web + C# 客户端（setImmediate 避免阻塞 SDK 迭代器）
      (msg) => setImmediate(() => bus.publishSdkMessage(msg as Record<string, unknown>)),
      onInit,
    );
    processResponses = currentProc.process;
    console.log('[cc-companion] 新会话已创建');
  }

  const server = createWSServer(
    CONFIG.port,
    CONFIG.host,
    CONFIG.token,
    // onEvent — RimWorld 游戏事件（C# 端已格式化文本）
    (wsMessage) => {
      const payload = (wsMessage.payload || {}) as Record<string, unknown>;
      const text: string = (payload.text as string) || '';

      // Game Bus：Agent 角色状态 → Web 头部显示
      if (wsMessage.event === 'agent.status' && text) {
        bus.publishAgentStatus(text);
        return;
      }

      // Game Bus：回显用户发言到所有 UI 客户端（不经 SDK，零延迟）
      if (text) {
        bus.publishUserMessage(text);
      }

      // Game Bus：殖民地统计 → Web 左侧 Pawns/Mood/Food 卡片
      const colonyStats = payload.colonyStats as Record<string, unknown> | undefined;
      if (colonyStats && (colonyStats.colonistCount !== undefined || colonyStats.avgMood !== undefined)) {
        bus.publishColonyStats(colonyStats as any);
        RuntimeState.lastColonyStats = colonyStats;
      }

      // C# Token 预算更新 → 刷新 companion 侧缓存并广播
      if (wsMessage.event === 'budget-update') {
        RuntimeState.tokenBudgetUsed = (payload.used as number) || 0;
        bus.publishBudgetStatus(
          RuntimeState.tokenBudgetLimit,
          RuntimeState.tokenBudgetUsed,
          RuntimeState.tokenBudgetAction,
          (payload.cacheRead as number) || 0,
          (payload.totalInput as number) || 0,
        );
        return;
      }

      // Token 预算检查（Companion 侧辅助 enforcement）
      if (RuntimeState.tokenBudgetLimit > 0 && RuntimeState.tokenBudgetUsed >= RuntimeState.tokenBudgetLimit) {
        if (RuntimeState.tokenBudgetAction === 'Block') {
          console.log(`[cc-companion] Token 预算已用尽(${RuntimeState.tokenBudgetUsed}/${RuntimeState.tokenBudgetLimit})，阻止消息`);
          bus.publishError(`Token 预算已用尽 (${RuntimeState.tokenBudgetUsed}/${RuntimeState.tokenBudgetLimit})`);
          return;
        }
        console.log(`[cc-companion] Token 预算已用尽，但为 Warn 模式，继续发送`);
      }

      // 随用户消息附带 thinking 模式切换
      const thinkingReq = (wsMessage as any).thinking;
      if (thinkingReq?.mode) {
        applyThinking(thinkingReq.mode, thinkingReq.effort, thinkingReq.tokens);
      }

      inputStream.enqueue({
        type: 'user',
        message: { role: 'user', content: text },
      });
      processResponses().catch(() => {});
    },
    // onStatusChange
    (status) => {
      if (status.status === 'connected') {
        console.log(`[cc-companion] RimWorld 已连接: ${typeof status.client === 'string' ? status.client : status.client?.name || 'unknown'}`);
        if (disidleTimer) { clearTimeout(disidleTimer); disidleTimer = null; }
        if (disidleInterval) { clearInterval(disidleInterval); disidleInterval = null; }
      } else if (status.status === 'disconnected') {
        console.log('[cc-companion] RimWorld 已断开');
        if (CONFIG.idleTimeout > 0) {
          const deadline = Date.now() + CONFIG.idleTimeout;
          disidleInterval = setInterval(() => {
            const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
            console.log(`[cc-companion] 断线倒计时: ${remaining}s 后自动退出`);
          }, 5000);
          disidleTimer = setTimeout(() => {
            if (disidleInterval) { clearInterval(disidleInterval); disidleInterval = null; }
            console.log(`[cc-companion] 断开后 ${CONFIG.idleTimeout / 1000}s 无重连，自动退出`);
            shutdown();
          }, CONFIG.idleTimeout);
        } else {
          console.log('[cc-companion] idleTimeout=0，断开后不自动退出');
        }
      }
    },
    // onAbort → 销毁当前 Runtime（抄 cc-gui：done() + return()）
    () => {
      inputStream.done();
      queryIterator.return?.();
      bus.publishAborted();
      startNewSession();
    },
    // onSetThinking → 动态切换（抄 cc-gui：setMaxThinkingTokens 无需中断）
    (mode: string, effort?: string, tokens?: number) => {
      applyThinking(mode, effort, tokens);
    },
  );

  // 初始化消息总线
  bus = new MessageBus((data) => server.broadcast(data));

  // HTTP 路由 — 聊天页面
  if (CONFIG.chatPageEnabled && chatPageHtml) {
    setupChatHttp(server.httpServer, chatPageHtml, CONFIG.projectPath);
  }

  // 启动首次处理
  processResponses().catch((err: any) => {
    console.error(`[cc-companion] SDK 处理异常: ${err.message}`);
  });

  // 6. 断开超时（仅断开后倒计时，启动后一直等首次连接）
  let disidleTimer: ReturnType<typeof setTimeout> | null = null;
  let disidleInterval: ReturnType<typeof setInterval> | null = null;

  // 7. PID 文件
  const pidFile = join(process.cwd(), '.pid');
  writeFileSync(pidFile, String(process.pid));
  console.log(`[cc-companion] PID ${process.pid} → ${pidFile}`);

  // 8. 关闭——直接 exit 走 RST，不产生 TIME_WAIT
  function shutdown() {
    console.log('\n[cc-companion] 正在关闭...');
    if (disidleTimer) { clearTimeout(disidleTimer); disidleTimer = null; }
    if (disidleInterval) { clearInterval(disidleInterval); disidleInterval = null; }
    try { unlinkSync(pidFile); } catch {}
    process.exit(0);
  }

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
  process.on('uncaughtException', (err) => {
    console.error(`[cc-companion] 未捕获异常: ${err.message}\n${err.stack}`);
  });
  process.on('unhandledRejection', (reason: any) => {
    console.error(`[cc-companion] 未处理的 Promise 拒绝: ${reason?.message || reason}\n${reason?.stack || ''}`);
  });

  console.log('[cc-companion] 就绪，等待 RimWorldMCP 连接...');
  console.log(`[cc-companion] WebSocket: ws://${CONFIG.host}:${CONFIG.port}`);
  if (CONFIG.chatPageEnabled) {
    console.log(`[cc-companion] 聊天页面: http://${CONFIG.host}:${CONFIG.port}/`);
  }
  console.log('');
}

function writeSettingFile(filePath: string, content: string): void {
  try {
    const dir = dirname(filePath);
    if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
    writeFileSync(filePath, content, 'utf-8');
    console.log(`[cc-companion] 写出 settings: ${filePath}`);
  } catch (err: any) {
    console.error(`[cc-companion] 写出 settings 失败: ${filePath} — ${err.message}`);
  }
}

main().catch((err: any) => {
  console.error(`[cc-companion] 致命错误: ${err.message}`);
  process.exit(1);
});
