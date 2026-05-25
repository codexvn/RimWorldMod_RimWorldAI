#!/usr/bin/env tsx
/**
 * Claude Code — RimWorldMCP 游戏 AI 助手
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
import { createSession, createResponseProcessor } from './session.js';

parseArgs(process.argv);

async function main(): Promise<void> {
  console.log('='.repeat(60));
  console.log('Claude Code — RimWorldMCP 游戏 AI 助手');
  console.log('='.repeat(60));
  console.log(`CWD: ${process.cwd()}`);
  console.log(`ARGV: ${process.argv.slice(2).join(' ')}`);
  console.log(`配置: host=${CONFIG.host} port=${CONFIG.port} model=${CONFIG.model} mcp=${CONFIG.mcpUrl}`);
  console.log(`会话目录: ${CONFIG.projectPath}`);
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

  // 3. 启动 WebSocket 服务器（先于 session，broadcast 需要引用）
  console.log('[cc-companion] 启动 WebSocket 服务器...');

  let processResponses: () => Promise<void>;
  const server = createWSServer(
    CONFIG.port,
    CONFIG.host,
    CONFIG.token,
    // onEvent — RimWorld 游戏事件（C# 端已格式化文本）
    (wsMessage) => {
      const text: string = (wsMessage.payload as any)?.text || '';
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

  // 4. 创建 SDK 会话
  console.log('[cc-companion] 创建 Claude SDK 会话...');
  const { inputStream, queryIterator } = createSession(sdk, CONFIG);

  // 5. 启动后台响应处理——Claude 消息广播回 RimWorld 聊天窗
  const proc = createResponseProcessor(
    queryIterator,
    CONFIG.projectPath,
    (msg) => server.broadcast(JSON.stringify(msg)),
  );
  processResponses = proc.process;
  processResponses().catch((err: any) => {
    console.error(`[cc-companion] SDK 处理异常: ${err.message}`);
  });

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

  // 8. 关闭——直接 exit 走 RST，不产生 TIME_WAIT
  function shutdown() {
    console.log('\n[cc-companion] 正在关闭...');
    if (connectTimer) { clearTimeout(connectTimer); connectTimer = null; }
    try { unlinkSync(pidFile); } catch {}
    process.exit(0);
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
