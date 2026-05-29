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
    const parts: string[] = [];
    parts.push('模型=' + (msg.model || '?'));
    parts.push('版本=' + (msg.claude_code_version || '?'));
    parts.push('Key=' + (msg.apiKeySource || '?'));
    parts.push('权限=' + (msg.permissionMode || '?'));
    parts.push('会话=' + (msg.session_id || '?'));
    if (msg.tools?.length) parts.push('工具=' + msg.tools.length);
    if (msg.mcp_servers?.length) {
      var mcpParts = msg.mcp_servers.map(function(s: any) { return s.name + '(' + s.status + ')'; });
      parts.push('MCP=[' + mcpParts.join(',') + ']');
    } else {
      parts.push('MCP=(无)');
    }
    const snapshot = parts.join(' | ');

    if (!lastInitSnapshot) {
      lastInitSnapshot = snapshot;
      console.log('[cc-companion] === SDK Init (首次) ===');
      console.log('[cc-companion] ' + snapshot);
      if (msg.mcp_servers) {
        for (var mi = 0; mi < msg.mcp_servers.length; mi++) {
          var s = msg.mcp_servers[mi];
          console.log('[cc-companion]   MCP[' + s.name + ']: ' + s.status + (s.status !== 'connected' ? ' ⚠' : ''));
        }
      }
    } else if (snapshot !== lastInitSnapshot) {
      console.log('[cc-companion] === SDK Init (变更) ===');
      console.log('[cc-companion] 旧: ' + lastInitSnapshot);
      console.log('[cc-companion] 新: ' + snapshot);
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
    // Agent Bus：SDK 所有消息 → Web + C# 客户端
    (msg) => bus.publishSdkMessage(msg as Record<string, unknown>),
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
      // Agent Bus：SDK 所有消息 → Web + C# 客户端
      (msg) => bus.publishSdkMessage(msg as Record<string, unknown>),
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
        console.log(`[user] ${text}`);
        bus.publishUserMessage(text);
      }

      // Game Bus：殖民地统计 → Web 左侧 Pawns/Mood/Food 卡片
      const colonyStats = payload.colonyStats as Record<string, unknown> | undefined;
      if (colonyStats && (colonyStats.colonistCount !== undefined || colonyStats.avgMood !== undefined)) {
        bus.publishColonyStats(colonyStats as any);
        RuntimeState.lastColonyStats = colonyStats;
      }

      // Game Bus：玩家 TODO → Web 右侧 TODO 面板
      const todoItems = payload.todoItems as Array<Record<string, unknown>> | undefined;
      if (todoItems) {
        bus.publishTodoState(todoItems);
        RuntimeState.lastTodoItems = todoItems;
      }
      if (wsMessage.event === 'todo-state') return;

      // C# Token 预算更新 → 刷新 companion 侧缓存并广播
      if (wsMessage.event === 'budget-update') {
        RuntimeState.tokenBudgetUsed = (payload.used as number) || 0;
        bus.publishBudgetStatus(
          RuntimeState.tokenBudgetLimit,
          RuntimeState.tokenBudgetUsed,
          RuntimeState.tokenBudgetAction,
        );
        return;
      }

      console.log(`[event] ${wsMessage.event || 'unknown'}: ${text.substring(0, 100)}`);

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
      } else if (status.status === 'disconnected') {
        console.log('[cc-companion] RimWorld 已断开');
        if (CONFIG.idleTimeout > 0) {
          disidleTimer = setTimeout(() => {
            console.log(`[cc-companion] 断开后 ${CONFIG.idleTimeout / 1000}s 无重连，自动退出`);
            shutdown();
          }, CONFIG.idleTimeout);
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

  // 7. PID 文件
  const pidFile = join(process.cwd(), '.pid');
  writeFileSync(pidFile, String(process.pid));
  console.log(`[cc-companion] PID ${process.pid} → ${pidFile}`);

  // 8. 关闭——直接 exit 走 RST，不产生 TIME_WAIT
  function shutdown() {
    console.log('\n[cc-companion] 正在关闭...');
    if (disidleTimer) { clearTimeout(disidleTimer); disidleTimer = null; }
    try { unlinkSync(pidFile); } catch {}
    process.exit(0);
  }

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

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
