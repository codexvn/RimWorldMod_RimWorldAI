#!/usr/bin/env tsx
/**
 * CC Companion — RimWorldMCP Claude Code 伴随进程
 *
 * 用法: tsx companion.ts [--port 19999] [--token xxx] [--model sonnet]
 * 详见 README.md
 */

import { writeFileSync, unlinkSync } from 'fs';
import { join } from 'path';
import { CONFIG, parseArgs } from './config.js';
import { setupApiAuth } from './auth.js';
import { loadClaudeSdk } from './sdk-loader.js';
import { createWSServer } from './ws-server.js';
import { gameEventToText } from './rimworld/context.js';
import { createSession, createResponseProcessor } from './session.js';

parseArgs(process.argv);

async function main(): Promise<void> {
  console.log('='.repeat(60));
  console.log('CC Companion — RimWorldMCP Claude Code 伴随进程');
  console.log('='.repeat(60));
  console.log(`配置: host=${CONFIG.host} port=${CONFIG.port} model=${CONFIG.model} mcp=${CONFIG.mcpUrl}`);
  console.log('');

  // 1. 认证
  setupApiAuth();

  // 2. 加载 SDK
  console.log('[cc-companion] 加载 Claude Agent SDK...');
  let sdk: any;
  try {
    sdk = await loadClaudeSdk();
  } catch (err: any) {
    console.error(`[cc-companion] SDK 加载失败: ${err.message}`);
    process.exit(1);
  }

  // 3. 创建 SDK 会话
  console.log('[cc-companion] 创建 Claude SDK 会话...');
  const { inputStream, queryIterator } = createSession(sdk, CONFIG);

  // 4. 启动后台响应处理
  const { process: processResponses } = createResponseProcessor(queryIterator, CONFIG.projectPath);
  processResponses().catch((err: any) => {
    console.error(`[cc-companion] SDK 处理异常: ${err.message}`);
  });

  // 5. 启动 WebSocket 服务器
  console.log('[cc-companion] 启动 WebSocket 服务器...');

  const server = createWSServer(
    CONFIG.port,
    CONFIG.host,
    CONFIG.token,
    // onEvent
    (wsMessage) => {
      const text = gameEventToText(wsMessage);
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
        if (connectTimer) { clearTimeout(connectTimer); connectTimer = null; }
      } else if (status.status === 'disconnected') {
        console.log('[cc-companion] RimWorld 已断开');
      }
    }
  );

  // 6. 连接超时
  let connectTimer: ReturnType<typeof setTimeout> | null = null;
  if (CONFIG.connectTimeout > 0) {
    connectTimer = setTimeout(() => {
      console.log(`[cc-companion] ${CONFIG.connectTimeout / 1000}s 内无客户端连接，自动退出`);
      shutdown();
    }, CONFIG.connectTimeout);
    console.log(`[cc-companion] 连接超时: ${CONFIG.connectTimeout / 1000}s (--no-connect-timeout 禁用)`);
  }

  // 7. PID 文件
  const pidFile = join(process.cwd(), '.pid');
  writeFileSync(pidFile, String(process.pid));
  console.log(`[cc-companion] PID ${process.pid} → ${pidFile}`);

  // 8. 优雅关闭
  function shutdown() {
    console.log('\n[cc-companion] 正在关闭...');
    if (connectTimer) { clearTimeout(connectTimer); connectTimer = null; }
    try { unlinkSync(pidFile); } catch {}
    inputStream.done();
    server.close();
    setTimeout(() => process.exit(0), 2000);
  }

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

  console.log('[cc-companion] 就绪，等待 RimWorldMCP 连接...');
  console.log(`[cc-companion] WebSocket: ws://${CONFIG.host}:${CONFIG.port}`);
  console.log('');
}

main().catch((err: any) => {
  console.error(`[cc-companion] 致命错误: ${err.message}`);
  process.exit(1);
});
