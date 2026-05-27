# 摄像头自动移动系统

## 概述

当 LLM 调用坐标工具或基于实体 ID 的工具时，框架自动将摄像头移动到操作目标区域，模拟人类玩家"看着目标操作"的视觉习惯。

## 架构

### 调用链

```
HTTP Request (transport 线程)
  → McpServer.HandleToolsCallAsync
    → ToolRegistry.ExecuteAsync(name, args)
      → tool.GetTargetRange(args)        ← 同步调用，在 HTTP 线程执行
      → if non-null: CameraHelper.MoveToRange(...)  ← dispatch 到主线程
      → tool.ExecuteAsync(args)          ← dispatch 到主线程
```

### 核心接口

`ITool.GetTargetRange(JsonElement? args)` — 从 LLM 参数中提取目标矩形，返回 `null` 表示无需移动摄像头。

```csharp
(int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args);
```

### 关键文件

| 文件 | 职责 |
|------|------|
| `Tools/ITool.cs` | 定义 `GetTargetRange` 接口方法 |
| `Tools/CameraHelper.cs` | `MoveToRange()` 缩放+平移；`FindPawnById()` / `FindThingById()` 实体查找 |
| `Tools/ToolRegistry.cs` | `ExecuteAsync()` 中调用 `GetTargetRange` 并触发摄像头移动 |

## 缩放规则

`CameraHelper.MoveToRange()` 的缩放逻辑：

1. 计算目标矩形覆盖所需的 `needSize`（带 5% padding）
2. 若 `needSize <= sizeRange.min`（1x 放得下）→ **1x 缩放**（最近距离）
3. 若 `needSize > sizeRange.min`（1x 放不下）→ 拉远到 `needSize`（上限 `MaxZoomOut = 40f`）
4. 使用 `CameraDriver.PanToMapLocAndSize(targetLoc, targetSize, 0.35f)` 动画平移，等待 400ms

**设计原则**：默认 1x 最近距离观察，仅目标区域超出屏幕时才拉远。不再根据当前缩放做"智能双向缩放"。

## GetTargetRange 实现模式

### 模式 A：坐标工具 — 单点（26 个）

仅接受 `pos_x`/`pos_y` 的工具，返回退化矩形 `(x, y, x, y)`。

```
designate_build, delete_zone, install_minified_thing, manage_stockpile_filter,
get_right_click_menu, set_grower_plant, set_temp_control, set_bed_owner_type,
move_pawn, apply_base_template
```

### 模式 B：坐标工具 — 矩形范围（14 个）

接受 `pos_x`/`pos_y` + 可选 `end_x`/`end_y`。`end_x` 和 `end_y` 各自独立处理（不提供则回退到 `pos_*`），`Math.Min`/`Math.Max` 归一化。

```csharp
int ex = x, ey = y;
if (args.TryGetProperty("end_x", ...)) ex = _ex;
if (args.TryGetProperty("end_y", ...)) ey = _ey;
return (Math.Min(x, ex), Math.Min(y, ey), Math.Max(x, ex), Math.Max(y, ey));
```

**适用工具**：
```
designate_deconstruct, designate_mine, designate_clear_plants, designate_plants_cut,
designate_harvest, designate_hunt, designate_slaughter, designate_tame,
designate_room, cancel_build, create_stockpile, create_growing_zone,
get_structure_layout, get_tile_detail, get_tile_grid, terrain_grid,
fertility_grid, temperature_grid, pollution_grid, take_screenshot,
plan_add, plan_remove, allow_item, forbid_item, claim_item
```

### 模式 C：单实体 ID — 单点（6 个）

通过 `thingIDNumber` 查找实体位置，返回退化矩形。

```csharp
var pawn = CameraHelper.FindPawnById(map, id);
if (pawn == null) return null;
return (pawn.Position.x, pawn.Position.z, pawn.Position.x, pawn.Position.z);
```

**适用工具**：
| 工具 | ID 参数 | 查找方式 |
|------|---------|---------|
| `drop_equipment` | `colonist_id` | `FindPawnById` |
| `force_surgery` | `colonist_id` | `FindPawnById` |
| `schedule_operation` | `colonist_id` | `FindPawnById` |
| `set_work_priority` | `colonist_id` | `FindPawnById` |
| `cancel_task` | `colonist_id`（可选） | `FindPawnById` |
| `draft_pawn` | `thing_id`（可选） | `FindPawnById` |

### 模式 D：双 Pawn — 并集（6 个）

殖民者对目标 Pawn 执行操作，框住双方位置。

```csharp
var a = CameraHelper.FindPawnById(map, idA);
var b = CameraHelper.FindPawnById(map, idB);
if (a == null || b == null) return null;
return (Min(a.Pos.x, b.Pos.x), Min(a.Pos.z, b.Pos.z),
        Max(a.Pos.x, b.Pos.x), Max(a.Pos.z, b.Pos.z));
```

**适用工具**：
| 工具 | 参数 1 | 参数 2 |
|------|--------|--------|
| `attack_pawn` | `colonist_id` | `target_id` |
| `arrest_pawn` | `doer_id` | `target_id` |
| `capture_pawn` | `doer_id` | `target_id` |
| `rescue_pawn` | `doer_id` | `target_id` |
| `force_attack` | `colonist_id` | `target_id` |
| `tend_now` | `doctor_id` | `patient_id` |

### 模式 E：Pawn + Thing — 并集（3 个）

殖民者与物品交互，框住双方。

| 工具 | 参数 1 | 参数 2 |
|------|--------|--------|
| `equip_pawn` | `colonist_id` | `thing_id` |
| `ingest_item` | `colonist_id` | `thing_id` |
| `pick_up_item` | `colonist_id` | `thing_id` |

### 模式 F：strip_pawn — 双查找并集

目标可能是活体 Pawn（`AllPawnsSpawned`）或尸体（`AllThings`）。先查活体，再查尸体作为回退。

```csharp
var target = (Thing?)CameraHelper.FindPawnById(map, targetId)
          ?? CameraHelper.FindThingById(map, targetId);
```

### 模式 G：force_dress — 三实体并集

框住衣匠 + 被穿衣者 + 衣物三个位置。

### 模式 H：haul / drop — 实体 + 可选坐标

- `haul_item`：框住 colonist + 目的地（有 `pos_x`/`pos_y` 用坐标，否则用物品位置）
- `drop_carried`：框住 colonist + 丢弃位置（有坐标用坐标，否则仅 colonist）
- `expand_zone`：框住 zone 位置 + 扩展矩形的并集

### 模式 I：uninstall_building — Thing 查找

通过 `thing_id` 查找 `AllThings`，返回建筑位置。

### 不移动摄像头的工具

以下工具 `GetTargetRange` 返回 `null`：
- 纯信息查询（`get_colonists`, `get_resources`, `list_recipes` 等）— 无位置概念
- `move_camera` — 自己处理摄像头移动，不触发自动移动
- 全局操作（`toggle_pause`, `allow_all_items`, `regenerate_map`）
- 元操作（`todo_*`, `submit_feedback`）

## 线程安全

`GetTargetRange` 在 HTTP 线程上同步执行（先于主线程 dispatch）。

- `Find.CurrentMap` — safe，简单引用读取，游戏过程中引用稳定
- `map.mapPawns.AllPawnsSpawned` — 返回 `List<Pawn>`，理论上主线程修改集合时存在并发风险
- `map.listerThings.AllThings` — 同上，返回 `List<Thing>`

**风险评估**：实际风险极低。
- `FindPawnById`/`FindThingById` 用 `foreach` 线性扫描，微秒级完成
- 主线程修改每帧突发而非持续
- 失败模式仅抛出 `InvalidOperationException`，被 `ToolRegistry.ExecuteAsync` 的 catch 捕获为错误返回给 MCP 客户端

## 实体查找辅助方法

`CameraHelper` 提供两个 `internal static` 方法，用 `foreach` 而非 LINQ 避免委托分配：

```csharp
internal static Pawn? FindPawnById(Map map, int id)
{
    foreach (var p in map.mapPawns.AllPawnsSpawned)
        if (p.thingIDNumber == id) return p;
    return null;
}

internal static Thing? FindThingById(Map map, int id)
{
    foreach (var t in map.listerThings.AllThings)
        if (t.thingIDNumber == id) return t;
    return null;
}
```

## Mod 设置

`McpModSettings.AutoMoveCamera`（默认 `true`）控制是否启用自动摄像头移动。设置 UI 路径：Options → Mod 设置 → RimWorld MCP → "调用工具时自动移动视角"。
