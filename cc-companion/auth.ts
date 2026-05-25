/**
 * API 认证 — 按优先级尝试多种凭据来源
 *
 * 优先级: env var > Mod 本地配置 > Claude CLI > CC GUI
 */

import { existsSync, readFileSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

export function setupApiAuth(): void {
  // 1. 环境变量
  if (process.env.ANTHROPIC_API_KEY || process.env.ANTHROPIC_AUTH_TOKEN) {
    console.log('[cc-companion] 认证: 环境变量');
    return;
  }

  // 2. Mod 本地配置: <modRoot>/claude-settings.json
  const localSettings = join(process.cwd(), '..', 'claude-settings.json');
  if (tryLocalSettings(localSettings)) return;

  // 3. Claude CLI 配置
  const claudeSettings = join(homedir(), '.claude', 'settings.json');
  if (existsSync(claudeSettings)) {
    try {
      const settings = JSON.parse(readFileSync(claudeSettings, 'utf8'));
      if (settings.apiKeyHelper) {
        console.log('[cc-companion] 认证: apiKeyHelper (由 SDK 处理)');
        return;
      }
    } catch {}
  }

  // 4. CC GUI 配置
  const codemossConfig = join(homedir(), '.codemoss', 'config.json');
  if (existsSync(codemossConfig)) {
    try {
      const config = JSON.parse(readFileSync(codemossConfig, 'utf8'));
      const activeProvider = config.claude?.activeProvider;
      const providers = config.claude?.providers || {};
      const provider = activeProvider ? providers[activeProvider] : null;
      if (provider?.env) {
        if (provider.env.ANTHROPIC_API_KEY) {
          process.env.ANTHROPIC_API_KEY = provider.env.ANTHROPIC_API_KEY;
          console.log('[cc-companion] 认证: CC GUI provider');
          return;
        }
        if (provider.env.ANTHROPIC_AUTH_TOKEN) {
          process.env.ANTHROPIC_AUTH_TOKEN = provider.env.ANTHROPIC_AUTH_TOKEN;
          console.log('[cc-companion] 认证: CC GUI provider (Bearer)');
          return;
        }
      }
    } catch {}
  }

  console.warn('[cc-companion] 警告: 未找到 API 认证配置。请设置 ANTHROPIC_API_KEY 环境变量或在 Mod 目录下创建 claude-settings.json。');
}

function tryLocalSettings(path: string): boolean {
  if (!existsSync(path)) return false;

  try {
    const settings = JSON.parse(readFileSync(path, 'utf8'));

    if (settings.apiKeyHelper) {
      console.log('[cc-companion] 认证: Mod 本地 apiKeyHelper');
      return true;
    }

    if (settings.ANTHROPIC_API_KEY) {
      process.env.ANTHROPIC_API_KEY = settings.ANTHROPIC_API_KEY;
      console.log('[cc-companion] 认证: Mod 本地 claude-settings.json');
      return true;
    }
    if (settings.ANTHROPIC_AUTH_TOKEN) {
      process.env.ANTHROPIC_AUTH_TOKEN = settings.ANTHROPIC_AUTH_TOKEN;
      console.log('[cc-companion] 认证: Mod 本地 claude-settings.json (Bearer)');
      return true;
    }
    if (settings.env?.ANTHROPIC_API_KEY) {
      process.env.ANTHROPIC_API_KEY = settings.env.ANTHROPIC_API_KEY;
      console.log('[cc-companion] 认证: Mod 本地 claude-settings.json (env)');
      return true;
    }
    if (settings.env?.ANTHROPIC_AUTH_TOKEN) {
      process.env.ANTHROPIC_AUTH_TOKEN = settings.env.ANTHROPIC_AUTH_TOKEN;
      console.log('[cc-companion] 认证: Mod 本地 claude-settings.json (env Bearer)');
      return true;
    }
  } catch (err: any) {
    console.warn(`[cc-companion] Mod 本地配置解析失败: ${err.message}`);
  }

  return false;
}
