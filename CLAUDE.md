# RimWorldMCP

MCP (Model Context Protocol) 服务器，将 RimWorld 游戏状态和操作暴露为 LLM 可调用的 Tool。作为 RimWorld mod DLL 内嵌运行。

**游戏源码**: `F:\RiderProjects\Assembly-CSharp\`（RimWorld 反编译 C# 源码，net472 Class Library）
**配套项目**: `F:\RiderProjects\RimWorldAgent\`（AI 助手桥接 Mod，通过 MCP 协议与本 Mod 通信）

## 项目结构

```
RimWorldMCP/
├── design/                                # 技术设计文档（架构细节、实现模式、设计理由）
│   ├── camera-system.md                   # 摄像头自动移动系统完整设计
│   └── tool-system.md                     # Tool 系统完整设计
├── RimWorldMCPMod.cs                      # Mod 入口：Mod 子类，管理设置窗口与生命周期
├── McpModSettings.cs                      # Mod 设置数据模型（日志/监听/OSS）
├── McpLog.cs                              # 统一日志（按级别过滤，输出到 Verse.Log）
├── GameComponent_McpServer.cs             # GameComponent 子类，管理 MCP 服务生命周期
├── McpCommandQueue.cs                     # 线程安全命令队列（ConcurrentQueue + TaskCompletionSource）
├── Transport/                             # 传输层
│   ├── ITransport.cs                      # 抽象接口
│   ├── SseTransport.cs                    # GET /sse + POST /message（HttpListener 后台线程）
│   ├── StreamableHttpTransport.cs         # POST /mcp（新版协议）
│   └── StdioTransport.cs                  # stdin/stdout（保留，不在游戏内使用）
├── Mcp/                                   # MCP 协议层
│   ├── McpServer.cs                       # JSON-RPC 调度：initialize/tools/list/tools/call/resources
│   ├── McpMessage.cs                      # 数据类型：请求/响应/Tool定义/资源
│   └── McpUtils.cs                        # MCP 工具函数（.mcp.json 构建等）
├── Helpers/                               # 辅助工具
│   ├── BuildingMaterialHelper.cs          # 建造材料计算
│   ├── GameTimeHelper.cs                  # 游戏时间格式化
│   ├── DialogHelper.cs                    # 弹框检测
│   └── NativeAlertHelper.cs              # 活跃警报快照
├── Tools/                                 # 90+ 个 Tool（真实 RimWorld API 调用）
│   ├── ITool.cs                           # Tool 接口 + ToolResult
│   ├── ToolRegistry.cs                    # 注册表 + 执行调度 + 资源映射（反射自动注册）
│   ├── ResourceCheckHelper.cs             # 建造资源检查辅助工具
│   └── Tool_*.cs                          # 各 Tool 实现
├── Bridge/                                # MCP 内部状态（SSE 事件推送用）
│   ├── GameContextProvider.cs             # 游戏上下文文本构建
│   └── McpInternalState.cs                # 内部状态，供 ToolRegistry 读取
├── Skills/                                # 领域知识 Skill 系统
│   ├── SkillInfo.cs                       # Skill 数据模型
│   ├── SkillRegistry.cs                   # 加载 .md 文件、解析 frontmatter
│   └── *.md                               # 11 个 Skill 文件
├── Harmony/                               # Harmony 补丁
│   ├── Hook_Notification.cs               # 拦截 Letter/Message/Alert → NotificationBus
│   ├── NotificationBus.cs                 # 通知总线，SSE 事件源
│   └── Notification.cs                    # 通知数据类
├── McpOssUploader.cs                      # 阿里云 OSS 截图自动上传
├── McpOssConfig.cs                        # OSS 配置数据
└── publish/About/
    └── About.xml                          # Mod 元数据
```

## 架构

单进程 mod 内嵌——LLM 通过 SSE 或 Streamable HTTP 连接游戏内的 MCP 服务。

- **net472 Library**：与 RimWorld Unity 运行时一致，`OutputType=Library`
- **引用 Assembly-CSharp.dll**：Tool 直接调用游戏 API（`Find.*`、`DefDatabase<>`、`PawnsFinder` 等）
- **GameComponent 入口**：反射自动发现，`StartedNewGame()` / `LoadedGame()` 时启动 HttpListener；`ExposeData()` 持久化 sessionId 到存档
- **线程安全**：只读 Tool 在 HttpListener 线程直接执行；写操作 Tool 通过 `McpCommandQueue` 调度到主线程
- **NuGet**: `System.Text.Json` 8.0.5 + `Aliyun.OSS.SDK` 2.14.x
- **输出**: `publish/1.6/Assemblies/RimWorldMCP.dll`

### IntVec3 坐标系统

RimWorld 的 `IntVec3(x, y, z)` 字段含义：
- `x` = 水平网格轴（东西方向）
- `y` = **海拔高度层**（地面=0，多层建筑用）
- `z` = **垂直网格轴**（南北方向）

2D 地图的有效网格范围是 `(x: 0 ~ map.Size.x-1, z: 0 ~ map.Size.z-1)`。

**Tool 参数映射规则**：MCP 用户的 `pos_x`/`pos_y`（2D 网格坐标）必须映射为 `new IntVec3(posX, 0, posY)`。
- `pos_y`（用户 Y 坐标）→ `IntVec3.z`（网格垂直轴）
- 海拔（`IntVec3.y`）始终为 0

**禁止**写成 `new IntVec3(posX, posY, 0)`——这会把用户 Y 坐标塞进海拔字段，所有建筑落到 z=0 行。

### 端口清理机制

RimWorld 返回主菜单时 `Game.Dispose()` 不通知 GameComponent，导致上一 Game 实例的 HttpListener（http.sys 内核级 URL 注册）残留。

- `GameComponent_McpServer.StopMcpService()` — 启动前调用，停止当前实例及静态残留的传输层
- `s_activeTransport` — 静态字段跨 Game 实例追踪活跃监听器
- `HttpListenerException` 错误码中文诊断（5=拒绝访问, 183=端口占用）
- `_transport` 在 `StartAsync()` 成功后才赋值，失败保持 null 允许下次重试

### 事件推送 (SSE)

`GameComponent_McpServer.PushGameEventsToSse()` 每帧从 `NotificationBus` 消费事件，通过 `SseTransport.BroadcastEvent` 推送。除 `game_event` 外还推送：
- `todo_state` — TodoManager 变更
- `deterioration_warning` — 物品腐坏警告
- `dialog_state` — 弹框出现/消失
- `advance_tick_active` / `advance_tick_complete` — Tool_AdvanceTick 生命周期

### Mod 设置

`RimWorldMCPMod`（继承 `Verse.Mod`）提供游戏内设置界面（Options → Mod 设置 → RimWorld MCP），设置项通过 `McpModSettings` 持久化：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| 日志级别 | Info | Debug / Info / Warn / Error 过滤 |
| MCP 监听地址 | 0.0.0.0 | 可设为 localhost / 内网 IP |
| MCP 端口 | 9877 | HTTP 监听端口 |
| 自动移动视角 | 开启 | AI 调用坐标工具时自动平移到目标位置 |
| OSS 上传 | 关闭 | 截图自动上传到阿里云 OSS |
| OSS Endpoint/Bucket/Key | - | 阿里云 OSS 访问配置 |
| 签名 URL | 开启 | 预签名 URL 有效期 24h |

## OSS 截图上传

`McpOssUploader` 在截图完成后自动上传到阿里云 OSS，支持预签名 URL（私有 Bucket）和公开 URL。

- 依赖：阿里云 OSS SDK（Aliyun.OSS.SDK.Net472）
- 配置：通过 Mod 设置界面或配置文件
- 触发：`take_screenshot` 工具调用后自动上传
- 返回：图片 URL（公开或预签名）

## 部署

```bash
# 构建
dotnet build

# 输出到 publish/1.6/Assemblies/
# 将整个 publish/ 目录放入 RimWorld Mods/RimWorldMCP/
# 或创建目录链接:
mklink /D F:\SteamLibrary\steamapps\common\RimWorld\Mods\RimWorldMCP F:\RiderProjects\RimWorldMCP\publish
```

游戏启动后，MCP 服务自动运行在 `http://localhost:9877`。

## Tool 清单（含 I18N 中文名 + 可达性检测）

中文名称参见 `publish/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`。以下为全部 112 个工具。

### 通用查询 (5)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_game_context` | 游戏全局状态快照 | `Find.CurrentMap`, `Find.TickManager`, `Find.ResearchManager` |
| `get_resources` | 资源库存报告 | `map.resourceCounter.AllCountedAmounts` |
| `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） | `PawnsFinder`, `map.wealthWatcher` |
| `toggle_pause` | 切换游戏暂停状态，恢复时设为最大速度 | `Find.TickManager.CurTimeSpeed` (入队) |
| `advance_tick` | 让游戏运行指定 tick 数后暂停返回状态，用于观察结果避免过度思考 | `Find.TickManager` (入队) |

### 网格查询 (6)
| Tool | 说明 | 参数 |
|------|------|------|
| `get_tile_detail` | 指定坐标范围详情（建筑/物品/植物/生物） | pos_x, pos_y, end_x, end_y |
| `get_tile_grid` | 文本化字符网格地图（64 种符号） | pos_x, pos_y, end_x, end_y |
| `fertility_grid` | 地面肥沃度视图（字符网格） | pos_x, pos_y, end_x, end_y |
| `terrain_grid` | 地形类型视图（字符网格） | pos_x, pos_y, end_x, end_y |
| `temperature_grid` | 温度分布视图（字符网格） | pos_x, pos_y, end_x, end_y |
| `pollution_grid` | 污染程度视图（字符网格） | pos_x, pos_y, end_x, end_y |

### 制造 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_recipes` | 列出可用配方 | `DefDatabase<RecipeDef>.AllDefs` |
| `create_production_bill` | 创建制造单据 | `BillStack.AddBill()` (入队) |
| `get_bills` | 查看工作单状态 | `map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()` |
| `manage_bill` | 管理单据（暂停/恢复/删除/优先级） | `bill.suspended`, `billStack.Delete()`, `billStack.Reorder()` (入队) |

### 建造 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_build` | 放置建造蓝图（单格） | `Designator_Build.DesignateSingleCell()` (入队) |
| `designate_room` | 快速建造矩形房间（墙+门+地板） | 批量 `Designator_Build` (入队) |
| `uninstall_building` | 拆卸建筑为微缩物品 | `Designator_Uninstall.DesignateThing()` |
| `install_minified_thing` | 安装微缩物品到指定坐标 | `GenConstruct.PlaceBlueprintForInstall()` |

### 标记 (5)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_mine` | 标记采矿（支持矩形范围） | `Designator_Mine` (入队) |
| `designate_plants_cut` | 标记植物砍伐（支持矩形范围，可过滤树种） | `Designator_PlantsCut` (入队) |
| `designate_harvest` | 标记作物收割（仅成熟作物） | `Designator_PlantsHarvest` (入队) |
| `designate_deconstruct` | 标记建筑拆除（矩形范围） | `Designator_Deconstruct` (入队) |
| `designate_clear_plants` | 标记清除非树木植物（草/灌木等） | `Designator_PlantsCut` (入队) |

### 存储/种植 (6)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `create_stockpile` | 创建物品储藏区（预设+优先级+筛选） | `Zone_Stockpile` (入队) |
| `create_growing_zone` | 创建种植区并设置植物类型 | `Zone_Growing` (入队) |
| `set_grower_plant` | 设置种植区作物类型 | 区域相关 API (入队) |
| `manage_stockpile_filter` | 管理储藏区物品筛选 | `StorageSettings` (入队) |
| `delete_zone` | 删除区域（储藏区/种植区） | `Zone.Deregister()` (入队) |
| `expand_zone` | 扩展已有区域的范围 | `Zone.AddCell()` (入队) |

### 装备管理 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `find_equipment` | 搜索地图可用武器/衣物，按类型品质分组 | `map.listerThings.AllThings` |
| `get_recommended_apparel` | 按游戏内置评分推荐衣物（复用 ApparelScoreGain） | `JobGiver_OptimizeApparel` |
| `get_recommended_weapon` | 按科技等级推荐武器（支持远程/近战过滤） | `map.listerThings.AllThings` |

### 截图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `take_screenshot` | 截取地图指定 X/Z 范围画面 | `ScreenshotTaker.TakeNonSteamShot()` (入队), 自动 OSS 上传 |

### 研究 (5)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_research_projects` | 列出研究项目（分页） | `DefDatabase<ResearchProjectDef>.AllDefsListForReading` |
| `get_research_progress` | 获取研究进度 | `Find.ResearchManager.GetProgress()` |
| `set_research_project` | 设置研究项目 | `Find.ResearchManager.SetCurrentProject()` (入队) |
| `stop_research` | 停止当前研究 | `Find.ResearchManager.StopProject()` (入队) |
| `get_research_speed` | 研究速度详情 | `Find.ResearchManager.GetResearchSpeed()` |

### 殖民者需求 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonists` | 殖民者信息 | `PawnsFinder.AllMaps_FreeColonistsSpawned` |
| `get_colonist_needs` | 详细需求状态 | `pawn.needs.AllNeeds` |
| `get_work_priorities` | 所有殖民者完整工作优先级表 | `pawn.workSettings.GetPriority()` |
| `set_work_priority` | 设置工作优先级 | `pawn.workSettings.SetPriority()` (入队) |

### 医疗 (5)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonist_health` | 健康报告 | `pawn.health.hediffSet.hediffs` |
| `schedule_operation` | 安排手术 | `billStack.AddBill(Bill_Medical)` (入队) |
| `tend_now` | 立即治疗指定殖民者 | `JobDefOf.TendPatient` (入队) |
| `force_surgery` | 强制执行指定手术 | `Bill_Medical` (入队) |
| `get_available_surgeries` | 列出可用手术（分页） | `RecipeDefOf` |

### 战斗 (6)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `equip_pawn` | 强制殖民者拾取并装备（Job 系统，自然走过去） | `JobDefOf.Equip` / `JobDefOf.Wear` (入队) |
| `draft_pawn` | 征召/解除征召 | `pawn.drafter.Drafted` (入队) |
| `get_defense_status` | 防御状态报告 | `pawn.equipment.Primary`, `map.listerBuildings` |
| `attack_pawn` | 攻击指定目标 | `JobDefOf.AttackMelee` / `JobDefOf.AttackStatic` (入队) |
| `force_attack` | 强制攻击（无视掩体/过墙） | `JobDefOf.AttackStatic` (入队) |
| `find_enemies` | 搜索地图上的敌人 | `map.mapPawns.AllPawnsSpawned` |

### 右键菜单操作 (10)
| Tool | 说明 | 操作 |
|------|------|------|
| `pick_up_item` | 拾取物品 | `JobDefOf.TakeInventory` (入队) |
| `drop_equipment` | 丢弃装备 | `pawn.equipment.Remove()` (入队) |
| `strip_pawn` | 剥除目标衣物/装备 | `JobDefOf.Strip` (入队) |
| `arrest_pawn` | 逮捕目标 | `JobDefOf.Arrest` (入队) |
| `rescue_pawn` | 救援倒地友方 | `JobDefOf.Rescue` (入队) |
| `capture_pawn` | 俘虏倒地敌人 | `JobDefOf.Capture` (入队) |
| `ingest_item` | 服食物品 | `JobDefOf.Ingest` (入队) |
| `force_dress` | 强制穿戴衣物 | `JobDefOf.Wear` (入队) |
| `haul_item` | 搬运物品到目标位置 | `JobDefOf.HaulToCell` (入队) |
| `drop_carried` | 放下手中物品 | `JobDefOf.DropEquipment` (入队) |

### 全局操作 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `allow_all_items` | 允许地图上所有被禁止的物品 | `CompForbiddable.Forbidden = false` (入队) |

### 贸易 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_faction_traders` | 列出可通讯的派系和商船 | `Find.FactionManager.AllFactionsVisible` |
| `trade_execute` | 执行交易（商船或定居点） | `TradeUtility` (入队) |

### 搜索 (4)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `search_map` | 按类型搜索地图事物（分页） | `map.listerThings.AllThings` |
| `find_pawn` | 搜索指定角色/生物（分页） | `map.mapPawns.AllPawnsSpawned` |
| `get_thing_def` | 查询物品定义详情 | `ThingDef` |
| `search_thing_def` | 按关键词搜索 ThingDef（分页） | `DefDatabase<ThingDef>.AllDefs` |

### 建筑布局 (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_structure_layout` | 查看建筑物内部结构布局 | `Building` / `CellRect` |
| `get_construction_status` | 查看建造项目进度 | `map.listerThings.ThingsOfDef` |

### 移动 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `move_pawn` | 移动角色到指定坐标 | `JobDefOf.Goto` (入队) |
| `move_camera` | 移动视角（本身不返回 GetTargetPos） | `Find.CameraDriver` |

### 弹框 (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_open_dialogs` | 列出当前打开的弹框 | `Find.WindowStack` |
| `select_dialog_option` | 选择弹框中的选项 | `WindowStack.TryRemove()` (入队) |

### 右键菜单 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_right_click_menu` | 生成指定坐标+殖民者的右键菜单 | `FloatMenuMakerMap.GetOptions()` |
| `select_right_click` | 执行右键菜单选项 | `FloatMenuOption.Chosen()` (入队) |

### 命令工具 (8)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `cancel_task` | 取消殖民者当前/排队任务 | `pawn.jobs.StopAll()` (入队) |
| `cancel_build` | 取消蓝图/框架/标记（矩形范围） | `t.Destroy(Cancel)` + `RemoveDesignation` (入队) |
| `designate_hunt` | 标记动物狩猎 | `AddDesignation(Hunt)` (入队) |
| `designate_slaughter` | 标记已驯服动物宰杀 | `AddDesignation(Slaughter)` (入队) |
| `designate_tame` | 标记野生动物驯服 | `AddDesignation(Tame)` (入队) |
| `forbid_item` | 禁止区域内物品 | `t.SetForbidden(true)` (入队) |
| `allow_item` | 允许区域内物品（精确范围版） | `t.SetForbidden(false)` (入队) |
| `claim_item` | 占有区域内物品/建筑为玩家派系 | `t.SetFaction(Faction.OfPlayer)` (入队) |

### 区域管理 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `set_bed_owner_type` | 设置床位类型（医疗/囚犯/殖民者） | `Building_Bed.Medical`, `CompAssignableToPawn` (入队) |
| `set_temp_control` | 设置温度控制设备 | `CompTempControl` (入队) |

### TODO 系统 (4)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `todo_add` | 添加待办任务 | `TodoManager` |
| `todo_delete` | 删除待办任务 | `TodoManager` |
| `todo_query` | 查询待办任务 | `TodoManager` |
| `todo_set_status` | 设置待办状态 | `TodoManager` |

### 任务 (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `list_quests` | 任务列表（分页，按状态过滤） | `Find.QuestManager.QuestsListForReading` |
| `accept_quest` | 接受任务 | `QuestUtility.AcceptQuest()` (入队) |

### 意识形态 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `convert_ideo` | 转换囚犯意识形态 | `Pawn_GuestTracker.ideo` (入队) |

### 计划系统 (3)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `plan_add` | 添加建造计划 | `PlanManager` |
| `plan_list` | 列出建造计划 | `PlanManager` |
| `plan_remove` | 删除建造计划 | `PlanManager` |

### 囚犯管理 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `recruit_prisoner` | 招募囚犯为殖民者 | `Pawn_GuestTracker` (入队) |

### 记忆系统 (4)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `add_memory` | 添加持久化记忆 | `MemoryManager` |
| `list_memories` | 列出所有记忆 | `MemoryManager` |
| `delete_memory` | 删除记忆 | `MemoryManager` |
| `update_memory` | 更新记忆优先级/内容 | `MemoryManager` |

### 游戏控制 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `restart_game` | 重新开始当前存档 | `GenScene` (入队，需确认) |

### 基地模板 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_base_templates` | 列出可用基地模板 | `BaseTemplateManager` |
| `apply_base_template` | 应用基地模板到地图 | `Designator_Build` 批量 (入队) |

### 反馈 (1)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `submit_feedback` | 向开发者提交反馈 | 文本收集 |

### 腐坏追踪 (1)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_deteriorating_items` | 腐坏/耐久降低物品清单 | `DeteriorationTracker` |

### 地图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `regenerate_map` | 重新生成当前地图（i_know_danger 确认） | `GetOrGenerateMapUtility` (入队) |

### Skill (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_skills` | 列出可用领域知识 | `SkillRegistry.GetAll()` |
| `active_skill` | 激活获取 Skill 内容 | `SkillRegistry.Get(name)` |

### 可达性检测

以下工具默认检查殖民者是否可达目标区域/位置，不可达到返回错误。传 `ignore_unreachable=true` 可跳过：

`designate_build`, `designate_room`, `designate_plants_cut`, `designate_harvest`, `designate_deconstruct`, `designate_clear_plants`, `create_stockpile`, `create_growing_zone`, `uninstall_building`, `install_minified_thing`

## Skill 系统

Skill 是领域知识文件（Markdown + YAML frontmatter），存放在 `Skills/` 目录。

| Skill | 内容 |
|-------|------|
| `equipment-crafting` | 装备制造策略、品质控制、材料选择 |
| `equipment-optimization` | 装备优化、品质提升、材料替换 |
| `colony-management` | 殖民地管理、工作分配、资源规划 |
| `combat-preparation` | 战斗准备、阵地部署、武器射程 |
| `base-building` | 基地布局设计、13x13 标准间、材料选择 |
| `research-management` | 科技树优先级、研究资源配置 |
| `medical-care` | 手术风险分析、植入体策略、药物使用 |
| `pawn-interaction` | 角色交互、社交关系、心情管理 |
| `resource-logistics` | 资源物流、存储管理、搬运优化 |
| `trade` | 贸易策略、商船管理、价格谈判 |
| `quest-management` | 任务管理、奖励评估、风险分析 |

## Claude Desktop 配置

```json
{
  "mcpServers": {
    "rimworld": {
      "type": "sse",
      "url": "http://localhost:9877/sse"
    }
  }
}
```

或使用 Streamable HTTP：

```json
{
  "mcpServers": {
    "rimworld": {
      "type": "http",
      "url": "http://localhost:9877/mcp"
    }
  }
}
```

## 开发

- net472，`System.Text.Json` + `Aliyun.OSS.SDK` NuGet 依赖
- 日志输出到 `Console.Error`（Transport 层）和 `Verse.Log`（GameComponent 层）
- Tool 返回值：`ToolResult` → `McpServer` 包装为 `{"content":[{"type":"text","text":"..."}]}`
- 写操作必须通过 `McpCommandQueue` 调度到主线程
- `dotnet build` → `publish/1.6/Assemblies/RimWorldMCP.dll`
- **坐标陷阱**：`IntVec3(x,y,z)` 中 `y` 是海拔，`z` 是网格垂直轴。MCP 用户的 `pos_y` 必须映射到 `IntVec3.z`，写 `new IntVec3(x, posY, 0)` 是 bug
- **HttpListener 陷阱**：`StartAsync` 中 `HttpListener.Start()` 可能抛 `HttpListenerException`（端口占用/权限不足），需提供中文诊断；`_transport` 要在 `StartAsync` 成功后才赋值；RimWorld 返回主菜单会导致 Game 对象被 Dispose 但 GameComponent 不通知，需静态字段跨实例清理
- **Session 隔离**：`cwd` 传入 SDK 后会被 sanitize 为目录名，checkpoint 落到 `~/.claude/projects/<sanitized-cwd>/`。不同存档的 `cwd` 不同 → sanitize 结果不同 → 隔离生效。不可改 SDK 内部的 `projects/` base path。

### I18N / 简体中文翻译

工具名称的简体中文翻译位于 `publish/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`，遵循 RimWorld Keyed 翻译格式：
- Key 格式：`RimWorldMCP_Tool_<tool_name>` → 中文名称
- 新增工具必须同步添加翻译条目
- 分页工具在中文名称后标注（分页）
- 工具名称应语义自包含：LLM 看到名称即知工具用途

### 开发规范

**1. 新增 Tool 先查游戏源码**

开发任何新 Tool 时，第一步是到 `F:\RiderProjects\Assembly-CSharp\` 反编译源码中追踪完整链路：用户在游戏界面点击 → Designator/Command → JobGiver/JobDriver → 游戏执行。理解原版如何处理输入验证、资源检查、失败路径，然后尽量复用游戏原有逻辑（Designator、Job、Bill 等），不要凭空造轮子。

**2. 坐标参数统一左上→右下**

所有 MCP Tool 的区域坐标参数使用 `pos_x/pos_y`（左上角）→ `end_x/end_y`（右下角）模式，禁止使用中心点+半径/宽高向外扩展的 API 设计。参考 `designate_mine` 的实现。
- `pos_x`/`pos_y` — 必填，区域起始角
- `end_x`/`end_y` — 可选，区域结束角（不提供则只操作单格）

**3. 所有 Tool 必须实现 GetTargetRange**

所有 Tool 必须 override `GetTargetRange(JsonElement? args)` 返回摄像头目标矩形，使 AI 调用工具时画面自动移动到操作目标。

**坐标类**：从 `args` 提取 `pos_x`/`pos_y`，有 `end_x`/`end_y` 的返回完整矩形并集（各自独立回退，`Math.Min`/`Math.Max` 归一化），只有单点的返回退化矩形 `(x, y, x, y)`。

**ID 类**：通过 `CameraHelper.FindPawnById(map, id)` / `FindThingById(map, id)` 查找实体位置。单实体返回退化矩形，多实体返回双方位置的并集。

**信息查询/全局操作/元操作**：返回 `null`（无位置概念，无需移动摄像头）。

非坐标非 ID 类 Tool 返回 `null`。`Tool_MoveCamera` 也返回 `null`（自己处理移动）。

详细实现模式见 `design/camera-system.md`。

**视角缩放规则**：默认 1x 缩放（最近距离），仅在目标区域超出 1x 视野时才拉远（上限 40 格）。详见 `design/camera-system.md`。

**4. 用 thingIDNumber 精确定位 Pawn/物品**

所有涉及殖民者（Pawn）或物品（Thing）的操作，参数统一使用 `thingIDNumber`（int）而非名称字符串匹配。名称有重名、翻译、截断风险，ID 唯一且稳定。
- 殖民者：`PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault(c => c.thingIDNumber == id)`
- 目标/物品：`map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == id)` 或 `map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id)`
- 工具之间传递引用时优先输出 `thingIDNumber`，让 LLM 在后续调用中精确回传
- 参考实现：`Tool_ArrestPawn.cs`、`Tool_AttackPawn.cs`、`Tool_EquipPawn.cs`

**5. 关注 LLM 缓存命中率**

Tool 的返回内容直接影响 prompt caching 的存活时间。大段返回会挤掉上下文中缓存的 system prompt，导致下次请求 cache miss，重新计费。

- **List 工具必须分页**：数据量可能超过 20 条的工具，提供 `page`/`page_size` 参数，默认每页 10。AI 按需翻页，不是一次性灌入
- **精简输出格式**：用表格而非段落，省略无意义的装饰文本。只输出 AI 决策必需的信息
- **查询类工具设计为「按需获取」**：如 `get_colonists` 返回摘要列表（名称+ID+心情），详细信息（健康/需求）由单独工具按 ID 查询
- **避免在结果中重复描述 Tool 自身的用法**：AI 已从 InputSchema 知道参数含义
- **价格判断**：一次 cache miss 相当于几千 token 的额外开销。如果一个 Tool 返回 2000 token，每次调用都可能触发 cache eviction，比翻 10 页（每页 200 token）的总成本更高

**6. 技术设计文档必须写入 design/ 目录**

凡是涉及架构决策、实现模式、多个文件协同配合的技术细节，必须在 `design/` 目录中建立对应的 `.md` 文件进行描述。修改了相关代码后，必须同步更新设计文档和 CLAUDE.md 中的对应描述。

- 设计文档聚焦 **WHY**（设计理由）和 **HOW**（实现模式），不重复代码
- 每个设计文档对应一个子系统（如 `camera-system.md`、`tool-system.md`）
- CLAUDE.md 保留概述和指向设计文档的引用，避免膨胀
- 新增子系统时必须在此规则下方更新文档索引表

**design/ 文档索引**：

| 文档 | 内容 |
|------|------|
| `design/camera-system.md` | 摄像头自动移动：GetTargetRange 接口、缩放规则、7 种实现模式、线程安全、实体查找 |
| `design/tool-system.md` | Tool 系统：ITool 接口、ToolRegistry 反射注册、执行流程、线程模型、McpCommandQueue |
