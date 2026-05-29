---
name: resource-logistics
description: 资源采集与存储物流。在标记采矿/砍伐/收割、创建存储区/种植区、处理腐坏物品时激活。
---

# 资源采集与存储物流

## 工具概览

| 工具 | 用途 |
|------|------|
| `designate_mine` | 标记采矿（岩石/矿物），支持矩形范围 |
| `designate_plants_cut` | 标记砍树（收获木材），可过滤树种 |
| `designate_harvest` | 标记收割成熟作物（仅 Standard 作物） |
| `designate_deconstruct` | 标记拆除建筑/墙体，支持矩形范围 |
| `designate_clear_plants` | 清除杂草/灌木（非树木） |
| `create_stockpile` | 创建储藏区，支持预设类型和优先级 |
| `create_growing_zone` | 创建种植区并指定作物 |
| `set_grower_plant` | 修改已有种植区的作物类型 |
| `manage_stockpile_filter` | 调整储藏区允许/禁止的物品类型 |
| `delete_zone` | 删除储藏区/种植区 |
| `expand_zone` | 扩展已有区域范围 |
| `haul_item` | 搬运指定物品到存储区或指定位置 |
| `drop_carried` | 让殖民者放下手中搬运的物品 |
| `allow_all_items` | 允许地图上所有被禁止的物品 |
| `get_deteriorating_items` | 查看正在腐坏/掉耐久的物品清单 |

## 资源采集工作流

### 采矿
```
1. get_tile_grid / get_tile_detail → 确认矿脉位置
2. designate_mine(pos_x, pos_y, end_x, end_y) → 标记开采区域
3. get_construction_status → 跟踪进度（可选）
```

### 伐木
```
1. get_tile_detail → 查看树木种类和成熟度
2. designate_plants_cut(pos_x, pos_y, end_x, end_y) → 标记砍伐
   - 默认只砍成熟树木，include_immature=true 包含未成熟
   - plant_defName 可指定树种（如 OakTree）
```

### 收割
```
1. get_tile_detail → 确认作物已成熟
2. designate_harvest(pos_x, pos_y, end_x, end_y) → 标记收割
   - 自动跳过未成熟作物，无需过滤
```

### 清草
```
1. designate_clear_plants(pos_x, pos_y, end_x, end_y) → 清理杂草灌木
   - 用于建设前清理用地、消除火灾隐患
```

## 存储策略

### 储藏区预设选择
| 预设 | 适用物品 | 说明 |
|------|----------|------|
| `food` | 食物、食材 | 靠近厨房/冷库 |
| `raw_resources` | 钢铁、木材、石材等 | 靠近生产区 |
| `manufactured` | 零部件、药品等成品 | 靠近生产区 |
| `weapons` | 武器 | 靠近防御阵地 |
| `apparel` | 衣物、护甲 | 靠近居住区 |
| `chunks` | 石块、钢渣块 | 靠近石材切割台 |
| `corpse` | 尸体 | 远离居住区 |
| `dumping` | 垃圾（岩石块等） | 室外即可 |

### 优先级策略
- `critical` — 紧急物资（医药、易腐食物快速入冷库）
- `important` — 高价值物品（零部件、药品）
- `normal` — 常规物资
- `low` — 低价值或无时效要求

### 筛选管理
- 使用 `manage_stockpile_filter` 精确控制存储区允许的物品
- 用 `get_thing_def` 查物品的 defName 后再添加到筛选

## 种植规划

### 创建种植区
```
1. get_structure_layout → 确认空地位置
2. create_growing_zone(pos_x, pos_y, end_x, end_y, plant_defName) → 创建并指定作物
```

### 作物选择
| 场景 | 推荐作物 | 原因 |
|------|----------|------|
| 初期食物 | `Plant_Rice` | 生长快（3天），快速解决温饱 |
| 长期食物 | `Plant_Corn` | 产量高（11天），劳动效率最高 |
| 应急食物 | `Plant_Potato` | 耐贫瘠，不挑土壤 |
| 药品 | `Plant_Healroot` | 基础药品，必备 |
| 布料 | `Plant_Cotton` | 制作衣物 |
| 高级纺织 | `Plant_Devilstrand` | 高防护布料（需研究） |
| 化工 | `Plant_Psychoid` | 精神茶原料 |
| 娱乐 | `Plant_Hops`, `Plant_Smokeleaf` | 啤酒/烟卷原料 |

### 季节考虑
- 生长季节长度影响作物选择（短季选 Rice，长季选 Corn）
- 冬季作物停止生长，确保季末前收割
- 水栽培盆 (`set_grower_plant`) 不受季节影响

## 腐坏防护

### 检查流程
```
1. get_deteriorating_items → 查看正在腐坏/掉耐久的物品
2. 对腐坏物品：
   - 食物 → 搬运到冷库 (haul_item)
   - 尸体 → 搬运到冷库或屠宰
   - 装备/材料 → 搬运到有屋顶的储藏区
3. allow_all_items → 如果物品被禁止，先允许拾取
```

### 预防措施
- 食物和易腐物必须放在有屋顶 + 冷藏的储藏区
- 室外物品不要露天存放（建屋顶或搬入仓库）
- 定期检查 `get_deteriorating_items` 防止损失

## 搬运管理
- `haul_item(colonist_id, thing_id)` — 指定殖民者搬运单件物品
- `drop_carried(colonist_id)` — 让殖民者放下手中物品
- 搬运前检查殖民者是否空闲（`get_colonists` 看当前任务）
- 大量搬运时提高搬运工作优先级（`set_work_priority`）

## 相关 Skill
- 基地布局 → [[base-building]]
- 殖民地管理 → [[colony-management]]
- 装备制造原材料 → [[equipment-crafting]]
