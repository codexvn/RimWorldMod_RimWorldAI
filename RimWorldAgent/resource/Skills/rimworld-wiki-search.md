---
name: rimworld-wiki-search
description: RimWorld Wiki 检索指南（Playwright MCP）。在需要查询游戏机制、物品数值、建筑条件、研究前置或事件规则时激活。
tags: ["概念/基础/学习助手"]
---

# RimWorld Wiki 检索指南（Playwright）

## 适用场景

- 不确定某个 RimWorld 机制、物品、建筑、研究、事件或意识形态规则
- 需要核对数值、前置条件、产物、工作类型、囚犯政策或战斗机制
- 当前游戏工具只能返回状态，无法解释背后的规则
- 用户询问百科类问题，而不是要求立即执行游戏操作

## 检索原则

**使用 Playwright MCP 直连 Wiki**，不经过搜索引擎或 WebFetch。
Playwright 是真实 Chromium 浏览器，能自动执行 Cloudflare JS 验证。

### 站点选择

| 站点 | URL | 特点 |
|------|-----|------|
| 中文灰机 Wiki | `https://rimworld.huijiwiki.com/wiki/页面名` | 中文内容，优先使用 |
| 英文官方 Wiki | `https://rimworldwiki.com/wiki/PageName` | 数据更完整，补充查询 |

### 标准流程

```
1. browser_navigate → Wiki URL（Cloudflare 自动通过）
2. browser_snapshot → 获取页面结构化文本
3. browser_evaluate → 提取表格数据为 JSON（可选）
4. 总结结论 → 转换为殖民地操作建议
```

### 常用页面

| 需求 | 中文 URL | 英文 URL |
|------|---------|---------|
| 战斗机制 | `/wiki/战斗` | `/wiki/Combat` |
| 武器数据 | `/wiki/武器` | `/wiki/Weapons` |
| 护甲数据 | `/wiki/护甲` | `/wiki/Armor` |
| 伤害类型 | `/wiki/伤害` | `/wiki/Damage` |
| 掩体 | `/wiki/掩护` | `/wiki/Cover` |
| 防御战术 | — | `/wiki/Defense_tactics` |
| 囚犯 | `/wiki/囚犯` | `/wiki/Prisoner` |
| 招募 | `/wiki/招募` | `/wiki/Recruitment` |
| 研究 | `/wiki/研究` | `/wiki/Research` |
| 作物 | `/wiki/作物` | `/wiki/Plants` |
| 温度 | `/wiki/温度` | `/wiki/Temperature` |

### 提取表格数据示例

```js
// browser_evaluate: 提取 wiki 表格
() => {
  const tables = document.querySelectorAll('table.wikitable, table.sortable');
  return Array.from(tables).map(t => {
    const rows = t.querySelectorAll('tr');
    return Array.from(rows).map(r =>
      Array.from(r.querySelectorAll('th,td')).map(c => c.textContent.trim())
    );
  });
}
```

## 信息处理流程

1. 先用游戏内 Tool 获取当前状态，明确问题发生在哪个对象/场景
2. 用 Playwright 打开对应 Wiki 页面，读取章节标题、表格、结论
3. 将外部资料转换为可执行的操作建议
4. 决策以游戏内 Tool 返回为准；Wiki 仅用于解释机制和补足背景
5. 若中英文 Wiki 冲突，以英文 Wiki + 游戏内实际数据为准

## 禁忌

- **不使用 WebFetch / WebSearch / Bash curl** → 这些工具无法通过 Cloudflare
- 不在战斗、火灾、医疗抢救等实时危机中等待 Wiki 查询
- 不复制长篇 Wiki 原文，只提炼决策相关结论
- 不把搜索摘要当完整规则；关键数值必须读到完整表格

## 与知识库结合

- 多次验证稳定的规则，写入记忆或沉淀为 Skill
- 只属于当前存档的判断（如某囚犯是否招募）写入记忆
- 查询过的外部结论保留来源 URL，便于复查
