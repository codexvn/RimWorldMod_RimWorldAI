/** SDK 会话 — AsyncStream + query + onMessage 回调 */

import { join, resolve, dirname } from 'path';
import { homedir } from 'os';
import { CONFIG, Thinking } from '../companion/config.js';
import { buildSystemPrompt } from '../rimworld/context.js';
import { Options, SYSTEM_PROMPT_DYNAMIC_BOUNDARY } from '@anthropic-ai/claude-agent-sdk';

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
    if (this.readResolve) { const r = this.readResolve; this.readResolve = undefined; r({ done: false, value }); }
    else this.queue.push(value);
  }

  done(): void {
    this.isDone = true;
    if (this.readResolve) { const r = this.readResolve; this.readResolve = undefined; r({ done: true, value: undefined as any }); }
  }
}

// ========== SDK 会话 ==========

export function createSession(sdk: any, abortController?: AbortController) {
  const inputStream = new AsyncStream<any>();

  const claudeMdExcludes: string[] = [];
  const addExclude = (p: string) => {
    const n = p.replaceAll('\\', '/');
    claudeMdExcludes.push(n);
    const lower = n.toLowerCase();
    if (lower !== n) claudeMdExcludes.push(lower);
  };
  let cursor = resolve(CONFIG.projectPath);
  while (true) {
    const parent = dirname(cursor);
    if (parent === cursor) break;
    cursor = parent;
    addExclude(join(cursor, 'CLAUDE.md'));
  }
  addExclude(join(homedir(), '.claude', 'CLAUDE.md'));

  const options = {
    cwd: CONFIG.projectPath,
    model: CONFIG.modelName || undefined,
    abortController,
    permissionMode: 'bypassPermissions',
    allowDangerouslySkipPermissions: true,
    disallowedTools: ['Bash', 'Write', 'Edit', 'NotebookEdit', 'EnterWorktree', 'ExitWorktree', 'CronCreate', 'CronDelete', 'CronList', 'ScheduleWakeup', 'AskUserQuestion', 'EnterPlanMode', 'ExitPlanMode', 'Skill', 'Task', 'TaskCreate', 'TaskUpdate', 'TaskList', 'TaskGet', 'TaskOutput', 'TaskStop', 'Glob', 'Grep', 'Read'],
    autoCompactEnabled: true,
    includePartialMessages: true,
    settingSources: CONFIG.settingSources as any,
    claudeMdExcludes,
    systemPrompt: [buildSystemPrompt(CONFIG.projectPath), SYSTEM_PROMPT_DYNAMIC_BOUNDARY],
    stderr: (data: string | Buffer) => {
      process.stderr.write(`[sdk] ${typeof data === 'string' ? data : data.toString()}`);
    },
  } as Options;

  const tm = Thinking.mode;
  if (tm === 'disabled') {
    options.thinking = { type: 'disabled' };
  } else {
    options.thinking = { type: 'adaptive' };
    if (Thinking.effort) options.effort = Thinking.effort;
  }

  const queryIterator = sdk.query({ prompt: inputStream, options });
  return { inputStream, queryIterator };
}

// ========== 响应处理 → onMessage ==========

export function createResponseProcessor(
  queryIterator: AsyncIterable<any>,
  onMessage: (msg: any) => void,
) {
  let processing = false;

  async function process(): Promise<void> {
    if (processing) return;
    processing = true;
    try {
      for await (const message of queryIterator) {
        onMessage(message);
      }
    } catch (err: any) {
      // AbortError 是正常中断，不打印错误
      if (err?.name === 'AbortError' || err?.message?.includes('aborted')) {
        console.log(`[bridge] process AbortError: name=${err?.name} msg=${err?.message}`);
        return;
      }
      console.error(`SDK 处理错误: ${err.message} name=${err?.name} stack=${err?.stack}`);
    }
    processing = false;
  }

  return { process };
}
