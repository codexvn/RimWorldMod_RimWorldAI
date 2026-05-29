---
name: pawn-interaction
description: 角色交互与应急处理。在执行逮捕、救援、俘虏、剥除、强制穿戴、服食、床位设置等操作时激活。
---

# 角色交互与应急处理

## 工具概览

| 工具 | 用途 | 关键约束 |
|------|------|----------|
| `arrest_pawn` | 逮捕殖民者/访客 | 需要囚犯床 |
| `rescue_pawn` | 救援倒地友方 | 需要医疗床 |
| `capture_pawn` | 俘虏倒地敌人 | 需要囚犯床 |
| `strip_pawn` | 剥除目标衣物装备 | 目标可以是活体或尸体 |
| `force_dress` | 强制给殖民者穿衣 | 需要 doer 和 target 两个殖民者 |
| `ingest_item` | 服食药物/食物 | 殖民者自动走过去 |
| `pick_up_item` | 拾取物品到背包 | 支持指定数量 |
| `drop_equipment` | 丢弃当前装备的武器 | 无需目标参数 |
| `set_bed_owner_type` | 设置床为殖民者/囚犯/奴隶 | 切换囚犯床会检查殖民者有无床可用 |
| `set_temp_control` | 调整空调/加热器温度或开关 | 用 thing_id 或坐标定位 |
| `get_open_dialogs` | 列出所有弹框及选项 | 返回编号供选择 |
| `select_dialog_option` | 选择弹框选项 | dialog_index + option_index |
| `move_pawn` | 移动殖民者到指定坐标 | 通过 Goto Job |

## 角色操作参考

使用 `thingIDNumber` 精确定位角色，不要用名称字符串匹配。

### 逮捕流程
```
1. set_bed_owner_type(owner_type="prisoner") → 确保有囚犯床
2. arrest_pawn(doer_id, target_id) → 执行逮捕
3. 逮捕后目标变为囚犯，可使用 capture_pawn 流程管理
```

### 救援流程
```
1. find_pawn / get_tile_detail → 找到倒地友方
2. rescue_pawn(doer_id, target_id) → 执行救援
   - 自动送往最近的可用医疗床
   - 确保有殖民者类型的床可用
```

### 俘虏流程
```
1. set_bed_owner_type(owner_type="prisoner") → 确保有囚犯床
2. capture_pawn(doer_id, target_id) → 执行俘虏
   - 仅限倒地敌人
   - 俘虏后需安排看守（Warden 工作）
```

### 战后缴获
```
1. find_enemies → 确认战斗结束
2. allow_all_items → 允许缴获物品
3. strip_pawn(doer_id, target_id) → 剥除敌人尸体
4. haul_item → 搬运缴获装备到仓库
5. 用 get_recommended_weapon/get_recommended_apparel 评估升级
```

### 强制装备
```
1. get_recommended_apparel / get_recommended_weapon → 找到最佳装备
2. equip_pawn(colonist_id, thing_id) → 普通装备
3. force_dress(doer_id, target_id, thing_id) → 强制给他人穿衣
   - doer 去拿衣物，给 target 穿上
   - 用于殖民者不自动更换衣物时
```

### 服食药物
```
1. get_colonist_health → 确认需求（感染、心情崩溃等）
2. ingest_item(colonist_id, thing_id) → 服食
   - 盘诺西林: 预防感染
   - 精神茶: 提升心情
   - 清醒药: 临时提升效率
```

## 床位管理

### 床位类型切换
```
set_bed_owner_type(pos_x, pos_y, owner_type)
```
- `colonist` — 普通殖民者床
- `prisoner` — 囚犯床（需要独立房间）
- `slave` — 奴隶床（仅 Ideology DLC）

切换为 prisoner 时会检查是否导致殖民者无床可用，传 `force=true` 跳过。

## 温度控制

```
set_temp_control(thing_id=xxx, target_temp=21, power_on=true)
```
- 空调: 目标温度设为制冷阈值（如 21°C）
- 加热器: 目标温度设为制热阈值
- 冷库: -5°C 以下冻结食物
- 不传 target_temp 则只切换电源

## 弹框交互

```
1. get_open_dialogs → 获取弹框列表和选项
2. select_dialog_option(dialog_index, option_index) → 选择
```

常见弹框类型：
- `FloatMenu` — 右键菜单（制造、搬运等选项）
- `Dialog_MessageBox` — 确认对话框（按钮 B/C/A）
- `Dialog_NodeTree` — 事件选项（任务/交易等）
- `Dialog_GiveName` — 命名对话框

## 右键菜单（万能入口）

`get_right_click_menu` + `select_right_click` 是游戏右键菜单的通用入口，覆盖 53 类 FloatMenu Provider + 130+ WorkGiver，**几乎所有角色对物品/坐标的操作都能通过它完成**，不需要逐一实现专用 MCP 工具。

### 用法

```
1. get_right_click_menu(colonist_id, thing_id|pos_x/pos_y) → 列出选项
2. select_right_click(option_index) → 执行
```

### 常见场景

| 场景 | 调用 |
|------|------|
| 安装机械师芯片 | `get_right_click_menu(colonist_id=1, thing_id=<芯片ID>)` → `select_right_click(0)` |
| 植入异种胚 | `get_right_click_menu(colonist_id=1, thing_id=<异种胚ID>)` → 选植入项 |
| 强制穿某件衣物 | `get_right_click_menu(colonist_id=2, thing_id=<衣物ID>)` → 选穿戴 |
| 优先搬运物品 | `get_right_click_menu(colonist_id=3, thing_id=<物品ID>)` → 选优先搬运 |
| 优先建造蓝图 | `get_right_click_menu(colonist_id=4, pos_x=56, pos_y=62)` → 选建造 |
| 服食药物 | `get_right_click_menu(colonist_id=5, thing_id=<药品ID>)` → 选服食 |
| 修理建筑 | `get_right_click_menu(colonist_id=6, thing_id=<建筑ID>)` → 选修理 |
| 驯服动物 | `get_right_click_menu(colonist_id=7, thing_id=<动物ID>)` → 选驯服 |
| 逮捕目标 | `get_right_click_menu(colonist_id=8, thing_id=<目标ID>)` → 选逮捕 |

### 禁用选项
带有 `[禁用]` 标记的选项不可执行，通常原因是技能不足、材料不够、不可达等。

### 对比专用工具
- 有专用工具优先用专用工具（`arrest_pawn`、`haul_item`、`equip_pawn` 等），参数更少更直接
- 专用工具未覆盖的操作（植入芯片、修理、开建筑、仪式等）→ 用右键菜单
- 不确定有什么操作可用时 → 先用右键菜单探查

## 移动控制
- `move_pawn(colonist_name, pos_x, pos_y)` — 移动到指定坐标
- `move_camera(pos_x, pos_y)` — 移动视角
- 战斗时先征召 (`draft_pawn`)，再移动部署

## 相关 Skill
- 战斗 → [[combat-preparation]]
- 医疗 → [[medical-care]]
- 装备优化 → [[equipment-optimization]]
- 殖民地管理 → [[colony-management]]
