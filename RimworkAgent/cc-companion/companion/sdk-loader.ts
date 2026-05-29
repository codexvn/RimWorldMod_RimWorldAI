/**
 * 加载 Claude Agent SDK — 从本地 node_modules
 */

import { existsSync, readFileSync } from 'fs';
import { join } from 'path';
import { pathToFileURL } from 'url';

const SDK_PACKAGE = '@anthropic-ai/claude-agent-sdk';

function resolveEntryFile(packageDir: string): string | null {
  const pkgJsonPath = join(packageDir, 'package.json');
  let candidate: string | null = null;

  if (existsSync(pkgJsonPath)) {
    try {
      const pkg = JSON.parse(readFileSync(pkgJsonPath, 'utf8'));
      const exports = pkg.exports;
      const root = exports?.['.'] ?? exports;
      if (typeof root === 'string') candidate = root;
      else if (root && typeof root === 'object') {
        candidate = root.import ?? root.default ?? root.module;
      }
      if (!candidate) {
        candidate = pkg.module ?? pkg.main;
      }
    } catch {}
  }

  if (candidate) return join(packageDir, candidate);

  for (const name of ['sdk.mjs', 'index.mjs', 'index.js', 'dist/index.js', 'dist/index.mjs']) {
    const p = join(packageDir, name);
    if (existsSync(p)) return p;
  }
  return null;
}

export async function loadClaudeSdk(): Promise<any> {
  const pkgDir = join(process.cwd(), 'node_modules', SDK_PACKAGE);

  if (!existsSync(pkgDir)) {
    throw new Error(
      `找不到 Claude Agent SDK。请在 Claude Code 目录执行:\n` +
      `  npm install`
    );
  }

  const entry = resolveEntryFile(pkgDir);
  if (!entry) {
    throw new Error(`无法解析 SDK 入口文件: ${pkgDir}`);
  }

  console.log(`[cc-companion] 加载 SDK: ${entry}`);
  const sdk = await import(pathToFileURL(entry).href);

  if (typeof sdk.query !== 'function') {
    throw new Error('SDK 缺少 query 函数');
  }

  console.log(`[cc-companion] SDK 已加载`);
  return sdk;
}
