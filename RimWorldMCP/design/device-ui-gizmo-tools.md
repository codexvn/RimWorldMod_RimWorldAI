# 设备 UI/Gizmo 工具计划

## 背景

当前 `set_temp_control` 只能直接设置带 `CompTempControl` 的温控设备，无法先发现“这个设备当前有哪些按钮、哪些状态、能执行哪些操作”。RimWorld 的设备按钮最终来自 `Thing.GetGizmos()` / `ThingComp.CompGetGizmosExtra()` 生成的 `Gizmo`/`Command`，因此通用设备工具应以 UI/Gizmo 为主入口，再用 Comp adapter 处理滑条、弹窗等参数型操作。

## 目标

新增两个 MCP Tool：

- `list_devices`：列出/搜索地图设备，支持关键字、defName、Comp、action_id、has_ui 和分页。
- `get_device_info`：获取设备/设备组的状态、全部 Comp、当前 UI/Gizmo 命令和可执行 `action_id`。
- `execute_device_action`：根据 `get_device_info` 返回的 `action_id` 操作指定设备/设备组，支持 `force_partial`。
- `manage_transporter_load`：运输器装载专用工具，首版支持 `status`、`clear_left_to_load`、`cancel_load`。

## 定位与选择上下文

两个工具都支持：

- `thing_id`：单个设备。
- `thing_ids`：设备数组，模拟 RimWorld 多选 UI。
- `pos_x`/`pos_y`：坐标定位，默认选择该格上第一个可识别设备。

本项目由 AI 自主管理，没有真人玩家需要保护当前选择。因此工具默认会接管 `Find.Selector`：

1. `Find.Selector.ClearSelection()`。
2. 选中目标设备/设备组。
3. 枚举或执行 UI/Gizmo。
4. 操作结束后保留目标选择，方便后续观察与连续操作。

## UI/Gizmo 发现

`get_device_info` 在主线程中调用目标 Thing 的 `GetGizmos()`，输出：

- 命令类型：`Command_Toggle`、`Command_Action`、其他 `Command`、普通 `Gizmo`。
- `label`、`desc`、`disabled`、`disabled_reason`。
- Toggle 的当前 `isActive()` 状态。
- 全部命令均列出，但 `DEV:` / Debug 命令过滤，不暴露给 LLM。

为避免本地化文案导致不稳定，action 分两类：

- `ui_toggle:<index>` / `ui_action:<index>`：当前查询结果内的 UI 命令序号，适合同一次查询后立即执行。
- `ui_toggle:<thingID>:<Command类型>:<label_hash>` / `ui_action:<thingID>:<Command类型>:<label_hash>`：稳定 ID，适合同一语言环境下跨次调用。
- 语义 adapter：如 `set_target_temp`、`adjust_target_temp`、`set_target_fuel_level`、`set_auto_refuel`、`eject_fuel`、`set_auto_build_transport_pod`。

## 执行策略

`execute_device_action` 重新定位并选中设备，然后重新枚举 UI/Gizmo：

- `Command_Toggle`：默认允许执行。无 `value` 时切换；传 boolean 时仅在目标状态与当前状态不一致时调用 `toggleAction()`。
- `Command_Action`：不盲目执行。仅执行明确安全 allowlist（温度调节/重置、发射台建造运输舱、运输器组选择导航）；复杂弹窗、目标选择、发射目的地选择等仅展示，不自动点击。
- 参数型操作：不模拟鼠标、不拖动 UI 滑条，使用 adapter 执行 UI 最终业务逻辑。

## 首版 adapter

| action_id | 目标 | value | 行为 |
|---|---|---|---|
| `set_power` | `CompPowerTrader` | boolean | 设置设备电源开关 |
| `set_target_temp` | `CompTempControl` | number | 设置目标温度，clamp 到 `-273.15~1000°C` |
| `adjust_target_temp` | `CompTempControl` | number | 在当前目标温度基础上增减 |
| `set_target_fuel_level` | `CompRefuelable` | number | 设置目标燃料量，clamp 到 `0~fuelCapacity` |
| `set_auto_refuel` | `CompRefuelable` | boolean | 设置自动添加燃料 |
| `eject_fuel` | `CompRefuelable` | 无 | `CanEjectFuel()` 校验后调用 `EjectFuel()` |
| `flick` | `CompFlickable` | boolean? | 省略时立即 `DoFlick()`；传 boolean 时设置到目标开关状态 |
| `set_auto_build_transport_pod` | `Building_PodLauncher` | boolean | 设置自动建造运输舱，开启时复刻原版 `CheckPlacePod()` 立即尝试放置蓝图 |
| `toggle_auto_build_transport_pod` | `Building_PodLauncher` | 无 | 切换自动建造运输舱 |

穿梭机 `CompShuttle` 的自动装载通过 `Command_Toggle` 暴露，优先用 UI Toggle 闭包执行，不反射写 private 字段。

## 只读状态

`get_device_info` 输出以下结构化状态：

- 基础信息：ID、label、defName、位置、派系、耐久。
- 所有 `AllComps` 类型名。
- `CompPowerTrader`：PowerOn、Off、PowerOutput。
- `CompTempControl`：TargetTemperature、operatingAtHighPower。
- `CompRefuelable`：Fuel、TargetFuelLevel、fuelCapacity、HasFuel、IsFull、allowAutoRefuel、CanEjectFuel。
- `CompFlickable`：SwitchIsOn。
- `CompPowerBattery`：StoredEnergy、StoredEnergyPct、容量。
- `CompBreakdownable`：BrokenDown。
- `CompTransporter` / `CompLaunchable` / `CompShuttle`：装载状态、质量、燃料/发射状态、自动装载状态。
- `Building_PodLauncher`：autoPlacePods。

## 部分执行

`thing_ids` 混合设备时，执行 adapter 默认要求所有设备都支持该 action；否则返回支持/不支持列表。传 `force_partial=true` 时，只对支持的设备执行，并在结果里列出跳过的设备。

## 覆盖层规则（`get_device_info` 组件状态区）

### 背景

选中建筑后游戏会画彩色覆盖层（空调蓝/红两端、太阳灯种植环、贸易信标投送区、炮塔射程、风力机风道）。覆盖层规则整合进 `get_device_info` 的「组件状态」区（由 `DeviceToolHelper.BuildOverlayRules` 生成，替代了原先残缺的数值版 `AppendRangeInfo`），原数值版只返回半径/计数且完全遗漏温度设备，现已用规则版替换。

### 设计原则：规则优先于坐标

- **不返回坐标列表**：Room.Cells 可能数十到上百格，列坐标会 token 爆炸。只输出 anchor/offset/radius/room_id/cell_count，让 LLM 按需用 `get_tile_grid` 查布局。
- 单设备输出 < 400 token，不受房间大小影响。

### 重要限制：覆盖层语义无法通用读取

覆盖层是**纯绘制抽象，没有运行时语义元数据**（源码验证）：
- `PlaceWorker.DrawGhost(ThingDef, IntVec3, Rot4, Color, Thing)` 是绘制唯一入口，签名无 effect/label 字段。
- `Thing.DrawExtraSelectionOverlays()` 只循环调 `PlaceWorker.DrawGhost`，不感知内容含义。
- 颜色常量 `ColorRoomHot/Cold` 只是 `Color` 结构体（rgba），"红=热蓝=冷"是人类约定。

**能力分层**：

| 能力 | 能否通用读取 | 数据源 |
|------|-------------|--------|
| 哪些设备有覆盖层 | ✅ 能 | `def.specialDisplayRadius`、`def.drawPlaceWorkersWhileSelected`、`def.PlaceWorkers` 类型枚举 |
| 几何（坐标/半径/范围） | ⚠️ 能，每种 PlaceWorker 算法须各自复刻 | 各 PlaceWorker/Comp 源码算法 |
| **语义（制热/制冷/种植/投送/射程）** | ❌ 不能 | 必须硬编码"类型→语义"映射表 |

**应对**：`effect` 字段诚实声明为本工具硬编码的语义知识库（非游戏读取），按"类型→语义"映射表分发。扩展 = 往表加一行。

### Cooler 方向规则（核心）

源码 `Building_Cooler.cs:17-18` + `IntVec3Utility.RotatedBy`：

- **制冷侧（蓝）** cell = `pos + IntVec3.South.RotatedBy(rot)` = **箭头反方向**相邻格 → 该格所在 Room 全部 cells
- **制热侧（红）** cell = `pos + IntVec3.North.RotatedBy(rot)` = **箭头同方向**相邻格 → 该格所在 Room 全部 cells
- 制热量 = 制冷能量 × 1.25
- **同房警告**：两 anchor 同处一密闭房间（`room==room2 && !UsesOutdoorTemperature`）→ 游戏画黄色警告（冷却无效）
- **室外房间**：anchor 在室外（`UsesOutdoorTemperature=true`）→ 不画房间级覆盖，但 cell 级热交换仍生效

输出三重表达覆盖 LLM 不同推理路径：`rotation(0-3)` + 方向名(North/East/South/West) + cell offset(dx,dz) + 一句话总结。

### 覆盖的 type 枚举

| type | effect | 几何 | 数据源 |
|------|--------|------|--------|
| `temp_room` | cool/heat | anchor + offset，扩展到 Room 全部 cells | `PlaceWorker_Cooler`/`_Heater`/`_CoolerSimple` |
| `trade_radius` | trade | 圆心+半径 7.9 + 区域连通(BFS,maxDepth=16) | `Building_OrbitalTradeBeacon.TradeableCellsAround` |
| `radius_ring` | grow/display_radius | 圆心 + `def.specialDisplayRadius` | `Thing.DrawExtraSelectionOverlays` 基类 |
| `light_radius` | light | 圆心 + `CompGlower.GlowRadius`（仅参考） | `CompGlower` |
| `attack_range` | attack_range | 环形 `[min_range, max_range]` | `Building_TurretGun.DrawExtraSelectionOverlays` |
| `noise_radius` | noise | 圆心 + `CompNoiseSource.Props.radius` | `CompNoiseSource` |
| `plant_harm_radius` | plant_harm | 圆心 + `CompPlantHarmRadius.CurrentRadius`（动态） | `CompPlantHarmRadius` |
| `wind_tunnel` | wind | 双侧矩形风道包围盒 | `WindTurbineUtility.CalculateWindCells` |

### GlowGrid 澄清

`GlowGrid.GroundGlowAt(cell)` 可读任意格当前光照，但是**全局累积渲染值**（混合太阳灯+其他灯+天空光+火堆，无法归因到单设备）。太阳灯选中看到的覆盖环是 `specialDisplayRadius` 种植环，不是 `GlowRadius`。故太阳灯输出两圈并标注：种植环(`specialDisplayRadius`, effect=grow, 覆盖层本身) + 光照声明半径(`GlowRadius`, 仅参考)。

## 文档与验证

实现后同步：

- `RimWorldMCP/resource/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`
- `RimWorldMCP/README.md`
- `RimWorldMCP/CLAUDE.md`
- 根目录 `CLAUDE.md` 设计文档索引

验证：

1. `dotnet build RimWorldAI.sln`
2. 工具列表包含 `list_devices` / `get_device_info` / `execute_device_action` / `manage_transporter_load`；`get_device_info` 对有覆盖层设备的输出含覆盖层规则块
3. 在游戏内验证冰箱/空调温度、燃料设备目标燃料和排出燃料、发射台自动建舱、穿梭机自动装载 Toggle、运输器状态与清空/取消装载。
