---
name: quest-management
description: 任务管理指南。在查看可用任务、接受任务、处理任务目标时激活。
---

# 任务管理指南

## 核心工具

| 工具 | 用途 |
|------|------|
| `list_quests` | 列出任务，支持 `filter=available/ongoing/all`，分页 |
| `accept_quest` | 接受指定 ID 的任务，可指定接受者殖民者 |

## 工作流

1. `list_quests(filter=available)` — 查看可接受的任务
2. 评估任务：检查可接受者、剩余时间、任务描述
3. `accept_quest(quest_id, colonist_id?)` — 指定殖民者接受任务（不传自动选）

## 任务状态

| 状态 | 含义 | 可操作 |
|------|------|--------|
| `NotYetAccepted` | 待接受 | `accept_quest` |
| `Ongoing` | 进行中 | 无需操作，等待完成 |
| `EndedSuccess` | 已完成 | 查看即可 |
| `EndedFailed` | 已失败 | 查看即可 |
| `EndedOfferExpired` | 已过期 | 无法接受 |

## 接受者选择

- 任务可能有特定接受条件（爵位、技能、派系关系等）
- `list_quests` 会显示可接受者
- 不指定 `colonist_id` 时自动选择第一个符合条件的殖民者
- 如果无人可接受：检查任务要求（`QuestPart_RequirementsToAccept`）

## 与其他 Skill 的关系

- **基地建造**: 任务奖励可能影响建造规划（如荣誉爵位需要谒见厅）。参见 [[base-building]]
- **战斗准备**: 任务常触发袭击（如摧毁营地）。参见 [[combat-preparation]]
- **贸易**: 交易请求任务需要特定货物。参见 [[trade]]
- **角色交互**: 难民/囚犯任务需要管理额外人员。参见 [[pawn-interaction]]
- **殖民地管理**: 任务驱动殖民地发展节奏。参见 [[colony-management]]
