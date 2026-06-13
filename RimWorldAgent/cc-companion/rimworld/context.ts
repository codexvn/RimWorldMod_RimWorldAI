/**
 * RimWorld 游戏上下文 — 系统提示词
 *
 * 从 Mod 根目录 Prompt.md 加载
 */

import { readFileSync, existsSync } from 'fs';
import { join } from 'path';

export function buildSystemPrompt(projectPath: string, skillsDescPath?: string): string {
  const promptPath = join(process.cwd(), 'Prompt.md');
  console.log(`[cc-companion] 加载 Prompt: ${promptPath}`);
  let content = readFileSync(promptPath, 'utf8');
  content = content.replace(/\{projectPath\}/g, projectPath);

  if (skillsDescPath && existsSync(skillsDescPath)) {
    const skillsDesc = readFileSync(skillsDescPath, 'utf8');
    content = content.replace(/\{skillsTable\}/g, skillsDesc);
    console.log(`[cc-companion] 加载 skills-desc: ${skillsDescPath} (${skillsDesc.length} bytes)`);
  } else {
    content = content.replace(/\{skillsTable\}/g, '(技能列表不可用，使用 get_skills 获取)');
  }

  return content;
}
