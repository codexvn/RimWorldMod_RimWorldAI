---
name: equipment-optimization
description: 装备优化指南。在为殖民者更换装备、评估武器/衣物升级时激活，帮助 LLM 做出最优装备分配决策。
---

# 装备优化指南

## 核心工具

### get_recommended_apparel
- 按游戏内置 `ApparelScoreGain` 评分排序，直接告诉你哪件衣物对殖民者提升最大
- 自动考虑：保暖需求、品质、护甲、耐久、着装方案过滤、性别/身体条件
- 返回表格含评分、品质、护甲值（利刃/钝击/隔热）、穿着层、是否已穿戴
- **评分 > 0 才有更换价值**，评分越高提升越大
- 调用示例：`get_recommended_apparel(colonist_id=123)`

### get_recommended_weapon
- 按科技等级 → 品质 → DPS 三级排序
- 支持 `ranged`（远程）和 `melee`（近战）两种过滤
- 远程武器显示：射程、伤害、预热/冷却时间
- 近战武器显示：伤害、冷却、DPS、穿甲
- 标记 `[已装备]` 避免重复更换
- 调用示例：`get_recommended_weapon(colonist_id=123, weapon_type="ranged")`

### find_equipment
- 全图搜索可用武器/衣物，按类型和品质分组
- 适合快速了解地图上有哪些装备类型
- 支持分页和品质过滤

## 使用流程

1. **检查当前装备**：先用 `get_colonists` 看全体殖民者的装备和技能
2. **按需查询推荐**：
   - 新殖民者加入 → 查武器 + 衣物推荐
   - 战后缴获 → 查武器推荐，可能有升级
   - 季节变化 → 查衣物推荐，保暖需求变化
3. **执行更换**：`equip_pawn(colonist_id, thing_id)` 装备推荐的第一项

## 装备分配优先级

1. **最佳武器给最高射击/格斗技能者**
2. **最佳护甲给前线战斗人员**
3. **非战斗人员**（研究/烹饪/种植）不需要最好装备
4. **已穿戴的装备评分不会再出现在推荐前列**，天然避免重复更换

## 常见场景

### 战后装备升级
```
1. find_enemies           → 确认战斗结束
2. allow_all_items        → 允许缴获物品
3. get_recommended_weapon → 逐个检查战斗人员
4. equip_pawn             → 执行升级
```

### 新殖民者装备
```
1. get_colonists              → 确认新成员
2. get_recommended_apparel    → 检查衣物需求
3. get_recommended_weapon     → 检查武器需求（远程+近战）
4. equip_pawn                 → 装备推荐物品
```

### 季节交替
```
1. get_colonists              → 查看全体状态
2. get_recommended_apparel    → 逐个检查保暖需求变化
3. equip_pawn                 → 更换适应新季节的衣物

## 相关 Skill
- 制造新装备 → [[equipment-crafting]]
- 战斗准备 → [[combat-preparation]]
- 殖民地管理 → [[colony-management]]
```
