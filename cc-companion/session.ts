/**
 * SDK 会话管理 — AsyncStream + query + 响应处理
 */

import { join } from 'path';
import { homedir } from 'os';
import { buildSystemPrompt } from './rimworld/context.js';
import type { CompanionConfig } from './config.js';
import {Options} from "@anthropic-ai/claude-agent-sdk";

// ========== AsyncStream ==========

export class AsyncStream<T = any> {
  private queue: T[] = [];
  private readResolve: ((v: IteratorResult<T>) => void) | undefined;
  private isDone = false;
  private started = false;

  [Symbol.asyncIterator](): AsyncIterator<T> {
    if (this.started) throw new Error('Stream can only be iterated once');
    this.started = true;
    return this;
  }

  async next(): Promise<IteratorResult<T>> {
    if (this.queue.length > 0) return { done: false, value: this.queue.shift()! };
    if (this.isDone) return { done: true, value: undefined as any };
    return new Promise((resolve) => { this.readResolve = resolve; });
  }

  enqueue(value: T): void {
    if (this.readResolve) {
      const r = this.readResolve;
      this.readResolve = undefined;
      r({ done: false, value });
    } else {
      this.queue.push(value);
    }
  }

  done(): void {
    this.isDone = true;
    if (this.readResolve) {
      const r = this.readResolve;
      this.readResolve = undefined;
      r({ done: true, value: undefined as any });
    }
  }
}

// ========== SDK 会话 ==========

function parseMcpHeaders(headersStr: string): Record<string, string> | undefined {
  if (!headersStr) return undefined;
  try {
    const obj = JSON.parse(headersStr);
    if (typeof obj === 'object' && !Array.isArray(obj)) return obj;
  } catch {}
  return undefined;
}

export function createSession(sdk: any, config: CompanionConfig) {
  const inputStream = new AsyncStream<any>();

  const mcpServerConfig: any = {
    type: config.mcpUrl.includes('/sse') ? 'sse' : 'http',
    url: config.mcpUrl,
  };
  const headers = parseMcpHeaders(config.mcpHeaders);
  if (headers) mcpServerConfig.headers = headers;

  const options = {
    cwd: config.projectPath,
    model: config.model,
    permissionMode: config.permissionMode,
    maxTurns: config.maxTurns,
    enableFileCheckpointing: true,
    env: {
      ...process.env,
      CLAUDE_CODE_ENTRYPOINT: 'cli',
      USER_TYPE: 'external',
    },
    settingSources: ['user', 'project', 'local'],
    permissions: {
      deny: ['*'],
      allow: ['mcp__*__*'],
    },
    mcpServers: {
      rimworld: mcpServerConfig,
    },
    systemPrompt: buildSystemPrompt(config.mcpUrl),
    stderr: (data: string | Buffer) => {
      const text = typeof data === 'string' ? data : data.toString();
      process.stderr.write(`[sdk] ${text}`);
    },
  } as Options;

  console.log(`[cc-companion] 项目目录: ${config.projectPath}`);
  console.log(`[cc-companion] MCP Server: ${config.mcpUrl}`);
  console.log(`[cc-companion] 会话将存储在: ${join(homedir(), '.claude', 'projects')}`);

  const queryIterator = sdk.query({ prompt: inputStream, options });
  console.log('[cc-companion] SDK 会话已创建');

  return { inputStream, queryIterator };
}

// ========== 后台响应处理 ==========

function sanitizePath(p: string): string {
  return p.replace(/[^a-zA-Z0-9]/g, '-').replace(/-+/g, '-');
}

export function createResponseProcessor(
  queryIterator: AsyncIterable<any>,
  cwd: string,
  onMessage?: (msg: any) => void,
) {
  let sessionId = 'pending';
  let processing = false;

  async function process(): Promise<void> {
    if (processing) { console.log('[cc-companion] processResponses 已在运行中，跳过'); return; }
    console.log('[cc-companion] processResponses 开始');
    processing = true;
    try {
      for await (const message of queryIterator) {
        const msgType: string = message?.type || 'unknown';

        if (msgType === 'system') {
          if (message.session_id && sessionId !== message.session_id) {
            sessionId = message.session_id;
            console.log(`[cc-companion] 会话 ID: ${sessionId}`);
            const sessionFile = join(homedir(), '.claude', 'projects',
              sanitizePath(cwd), `${sessionId}.jsonl`);
            console.log(`[cc-companion] 会话文件: ${sessionFile}`);
          }
        }

        if (msgType === 'assistant' || msgType === 'user') {
          onMessage?.(message);
          const content = message.message?.content;
          if (Array.isArray(content)) {
            for (const block of content) {
              if (block.type === 'text') {
                const text = block.text?.substring(0, 200) || '';
                console.log(`[assistant] ${text}${block.text?.length > 200 ? '...' : ''}`);
              } else if (block.type === 'tool_use') {
                console.log(`[tool_use] ${block.name}`);
              }
            }
          }
        }

        if (msgType === 'result') {
          onMessage?.(message);
          const summary = message.subtype === 'success'
            ? '执行成功' : `执行失败: ${message.errors?.join(', ') || 'unknown'}`;
          console.log(`[result] ${summary}`);
        }
      }
    } catch (err: any) {
      console.error(`[cc-companion] SDK 响应处理错误: ${err.message}`);
    }
    processing = false;
    console.log('[cc-companion] processResponses 结束');
  }

  return { process, getSessionId: () => sessionId };
}
