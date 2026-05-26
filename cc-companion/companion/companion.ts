#!/usr/bin/env tsx
/**
 * Claude Code — RimWorldMCP 游戏 AI 助手
 *
 * 用法: tsx companion.ts [--port 19999] [--token xxx] [--model sonnet]
 * 详见 README.md
 */

import { writeFileSync, unlinkSync, existsSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { CONFIG, parseArgs } from './config.js';
import { loadClaudeSdk } from './sdk-loader.js';
import { createWSServer } from '../bridge/ws-server.js';
import { createSession, createResponseProcessor } from '../bridge/session.js';
import { getChatPageHtml } from '../chat/chat-page.js';
import { setupChatHttp } from '../chat/chat-http.js';

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

  const onInit = (msg: any) => {
    console.log('');
    console.log('[cc-companion] SDK 初始化完成:');
    console.log(`  模型: ${msg.model || '?'}`);
    console.log(`  版本: ${msg.claude_code_version || '?'}`);
    console.log(`  API Key: ${msg.apiKeySource || '?'}`);
    console.log(`  权限模式: ${msg.permissionMode || '?'}`);
    console.log(`  数据目录: ${msg.cwd || '?'}`);
    console.log(`  会话 ID: ${msg.session_id || '?'}`);
    if (msg.mcp_servers?.length) {
      for (const s of msg.mcp_servers) {
        console.log(`  MCP 服务: ${s.name} (${s.status})`);
      }
    }
    if (msg.tools?.length) {
      console.log(`  工具数: ${msg.tools.length}`);
    }
    console.log('');
  };

  let abortController = new AbortController();
  let { inputStream, queryIterator } = createSession(sdk, CONFIG, abortController);
  let currentProc = createResponseProcessor(
    queryIterator,
    CONFIG.projectPath,
    (msg) => server.broadcast(JSON.stringify(msg)),
    onInit,
  );
  let processResponses = currentProc.process;

  function startNewSession() {
    abortController = new AbortController();
    const result = createSession(sdk, CONFIG, abortController);
    inputStream = result.inputStream;
    queryIterator = result.queryIterator;
    currentProc = createResponseProcessor(
      queryIterator,
      CONFIG.projectPath,
      (msg) => server.broadcast(JSON.stringify(msg)),
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

      // 提取结构化殖民地统计并直接广播给聊天页（不经 SDK 转发）
      const colonyStats = payload.colonyStats as Record<string, unknown> | undefined;
      if (colonyStats && (colonyStats.colonistCount !== undefined || colonyStats.avgMood !== undefined)) {
        server.broadcast(JSON.stringify({ type: 'colony-stats', ...colonyStats }));
      }

      console.log(`[event] ${wsMessage.event || 'unknown'}: ${text.substring(0, 100)}`);
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
        if (idleTimer) { clearTimeout(idleTimer); idleTimer = null; }
      } else if (status.status === 'disconnected') {
        console.log('[cc-companion] RimWorld 已断开');
        if (CONFIG.idleTimeout > 0) {
          idleTimer = setTimeout(() => {
            console.log(`[cc-companion] 断开后 ${CONFIG.idleTimeout / 1000}s 无重连，自动退出`);
            shutdown();
          }, CONFIG.idleTimeout);
        }
      }
    },
    // onAbort
    () => {
      abortController.abort();
      server.broadcast(JSON.stringify({ type: 'result', subtype: 'aborted' }));
      startNewSession();
    },
  );

  // HTTP 路由 — 聊天页面
  if (CONFIG.chatPageEnabled && chatPageHtml) {
    setupChatHttp(server.httpServer, chatPageHtml);
  }

  // 启动首次处理
  processResponses().catch((err: any) => {
    console.error(`[cc-companion] SDK 处理异常: ${err.message}`);
  });

  // 6. 连接/断开超时
  let idleTimer: ReturnType<typeof setTimeout> | null = null;
  let disidleTimer: ReturnType<typeof setTimeout> | null = null;
  if (CONFIG.idleTimeout > 0) {
    idleTimer = setTimeout(() => {
      console.log(`[cc-companion] 空闲超时：${CONFIG.idleTimeout / 1000}s 内无客户端连接，自动退出`);
      shutdown();
    }, CONFIG.idleTimeout);
    console.log(`[cc-companion] ${CONFIG.idleTimeout / 1000}s 内无客户端连接将自动退出`);
  }

  // 7. PID 文件
  const pidFile = join(process.cwd(), '.pid');
  writeFileSync(pidFile, String(process.pid));
  console.log(`[cc-companion] PID ${process.pid} → ${pidFile}`);

  // 8. 关闭——直接 exit 走 RST，不产生 TIME_WAIT
  function shutdown() {
    console.log('\n[cc-companion] 正在关闭...');
    if (idleTimer) { clearTimeout(idleTimer); idleTimer = null; }
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
