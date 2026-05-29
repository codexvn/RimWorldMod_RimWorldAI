/**
 * RimWorld 游戏上下文 — 系统提示词
 *
 * 从 Mod 根目录 Prompt.md 加载
 */

import { readFileSync } from 'fs';
import { join } from 'path';

export function buildSystemPrompt(): string {
  const promptPath = join(process.cwd(), 'Prompt.md');
  console.log(`[cc-companion] 加载 Prompt: ${promptPath}`);
  return readFileSync(promptPath, 'utf8');
}
