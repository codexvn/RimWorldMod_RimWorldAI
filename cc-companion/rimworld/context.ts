/**
 * RimWorld 游戏上下文 — 系统提示词
 *
 * 从 Mod 根目录 Prompt.md 加载
 */

import { readFileSync } from 'fs';
import { join } from 'path';

export function buildSystemPrompt(mcpUrl: string): string {
  const modRoot = join(process.cwd(), '..');
  const promptPath = join(modRoot, 'Prompt.md');
  console.log(`[cc-companion] 加载 Prompt: ${promptPath}`);
  const prompt = readFileSync(promptPath, 'utf8');
  return `${prompt}\n\n### 当前 MCP Server\n\n${mcpUrl}`;
}
