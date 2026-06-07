# RimWorld MCP

将 RimWorld 游戏状态和操作暴露为 AI 可调用的工具接口——AI 殖民地的游戏层基础设施。

## 这是什么？

RimWorld MCP 是一个后台服务 Mod，它在游戏内启动一个 MCP (Model Context Protocol) 服务器，将游戏的几乎所有操作——建造、制造、战斗、医疗、贸易、种植等——暴露为标准化的 AI 工具。AI 可以像玩家一样读取游戏状态、下达指令。

**注意：本 Mod 本身不包含 AI。** 你需要同时订阅 [RimWorld Agent](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261723) 来获得 AI 自主管理能力。

## 功能特性

- **100+ 游戏工具**：覆盖殖民者管理、建造蓝图、生产订单、战斗指挥、医疗手术、贸易等全部游戏操作
- **实时事件推送**：突袭、商队、疾病等游戏事件实时通知 AI
- **网格地图查询**：字符网格视图（地形/温度/肥沃度/污染），AI 可精确感知地图
- **截图上报**：AI 可截取游戏画面辅助决策
- **后台运行**：无 UI，纯后台服务，不影响正常游戏体验
- **线程安全**：只读操作即时响应，写操作调度到主线程安全执行
- **分块地图渲染**：地图按 32×32 Chunk 分块，AI 先获取 Chunk 列表再按需查询各块，Token 消耗可精算
- **三种压缩算法**：未压缩 / RLE / 行引用+RLE，大幅降低网格工具 Token 消耗，提升 prompt cache 命中率

## 分块地图与压缩

### 工作流

```
list_chunks(pos_x, pos_y, end_x, end_y) → chunk_id 列表: "0_0", "1_0", ...
get_tile_grid(chunk_id="0_0")            → Chunk(0,0) 压缩内容
get_tile_grid(chunk_id="1_0")            → Chunk(1,0) 压缩内容  （可并行查询）
```

LLM 先调用 `list_chunks` 获取目标区域覆盖的 Chunk ID 列表，再按需逐一查询各 Chunk 内容。同 chunk_id 同输出格式，天然适配 LLM prompt cache。

### 压缩算法

| 算法 | 原理 | Token 节省 |
|------|------|-----------|
| **未压缩** | `L00=#.....##..` 逐字输出 | 基准 |
| **RLE**（默认） | `L00=#1.30#1` 连续相同字符合并 | ~60-80% |
| **行引用+RLE** | `*L{row}` 引用相同行 + RLE | ~70-90% |

网格字符均为 Unicode 符号，不含 `0-9`，十进制数字无二义性——读到字符后，后续数字即为其重复次数。

### 输出格式

```
## get_tile_grid  Chunk(2,1)  世界(64,32)-(95,63)  [32x32]
## 压缩: RLE  字典Hash: a3f2b1c0

L00=#32
L01=#1.14D1.14#1
...

## 图例
#=墙  .=空地  B=床  D=门
```

### 符号字典

固定映射 ~150 常用建筑/物品 → 直观字符（`#`=墙、`D`=门、`B`=床），冷门 Def 自动从 Unicode 池动态分配。字典 Hash 随 Mod 集变化自动检测，按需刷新。

### 配置

| 设置 | 默认值 | 说明 |
|------|--------|------|
| Chunk 宽度 | `32` | 分块宽度（格） |
| Chunk 高度 | `32` | 分块高度（格） |
| 网格压缩 | RLE | 未压缩 / RLE / 行引用+RLE |

## 依赖

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)

## 配置

游戏内打开 `选项 → Mod 设置 → RimWorld MCP`：

| 设置 | 默认值 | 说明 |
|------|--------|------|
| 日志级别 | Info | 调试时可设为 Debug |
| MCP 监听地址 | `0.0.0.0` | 本地使用保持默认 |
| MCP 端口 | `9877` | 避免与其他服务冲突 |
| 自动移动视角 | 开启 | AI 操作时镜头自动移动到目标位置 |
| OSS 截图上传 | 关闭 | 截图自动上传阿里云 OSS（需配置） |

## 工具清单

### 通用查询 (7)
| `get_game_context` | 游戏全局状态快照 |
| `get_resources` | 资源库存报告 |
| `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） |
| `toggle_pause` | 切换游戏暂停状态 |
| `advance_tick` | 让游戏运行指定 tick 数后暂停返回状态 |
| `get_mcp_latency` | 探查 Agent ↔ 游戏延迟 |
| `check_map_loaded` | 检查游戏和地图加载状态 |

### 网格查询 (6)
| `get_tile_detail` | 指定坐标范围详情（建筑/物品/植物/生物） |
| `get_tile_grid` | 文本化字符网格地图 |
| `fertility_grid` | 地面肥沃度视图 |
| `terrain_grid` | 地形类型视图 |
| `temperature_grid` | 温度分布视图 |
| `pollution_grid` | 污染程度视图 |

### 制造 (4)
| `list_recipes` | 列出可用配方 |
| `create_production_bill` | 创建制造单据 |
| `get_bills` | 查看工作单状态 |
| `manage_bill` | 管理单据（暂停/恢复/删除/优先级） |

### 建造 (4)
| `designate_build` | 放置建造蓝图 |
| `designate_room` | 快速建造矩形房间（墙+门+地板） |
| `uninstall_building` | 拆卸建筑为微缩物品 |
| `install_minified_thing` | 安装微缩物品到指定坐标 |

### 标记 (5)
| `designate_mine` | 标记采矿（矩形范围） |
| `designate_plants_cut` | 标记植物砍伐 |
| `designate_harvest` | 标记作物收割 |
| `designate_deconstruct` | 标记建筑拆除 |
| `designate_clear_plants` | 标记清除非树木植物 |

### 存储/种植 (6)
| `create_stockpile` | 创建物品储藏区 |
| `create_growing_zone` | 创建种植区 |
| `set_grower_plant` | 设置种植区作物 |
| `manage_stockpile_filter` | 管理储藏区物品筛选 |
| `delete_zone` | 删除区域 |
| `expand_zone` | 扩展区域范围 |

### 装备 (3)
| `find_equipment` | 搜索地图可用武器/衣物 |
| `get_recommended_apparel` | 按游戏评分推荐衣物 |
| `get_recommended_weapon` | 按科技等级推荐武器 |

### 研究 (5)
| `list_research_projects` | 列出研究项目 |
| `get_research_progress` | 获取研究进度 |
| `set_research_project` | 设置研究项目 |
| `stop_research` | 停止当前研究 |
| `get_research_speed` | 研究速度详情 |

### 殖民者 (4)
| `get_colonists` | 殖民者信息 |
| `get_colonist_needs` | 详细需求状态 |
| `get_work_priorities` | 工作优先级表 |
| `set_work_priority` | 设置工作优先级 |

### 医疗 (5)
| `get_colonist_health` | 健康报告 |
| `schedule_operation` | 安排手术 |
| `tend_now` | 立即治疗指定殖民者 |
| `force_surgery` | 强制执行指定手术 |
| `get_available_surgeries` | 列出可用手术 |

### 战斗 (6)
| `equip_pawn` | 强制殖民者拾取并装备 |
| `draft_pawn` | 征召/解除征召 |
| `get_defense_status` | 防御状态报告 |
| `attack_pawn` | 攻击指定目标 |
| `force_attack` | 强制攻击（无视掩体） |
| `find_enemies` | 搜索地图上的敌人 |

### 右键菜单操作 (10)
| `pick_up_item` | 拾取物品 |
| `drop_equipment` | 丢弃装备 |
| `strip_pawn` | 剥除目标衣物/装备 |
| `arrest_pawn` | 逮捕目标 |
| `rescue_pawn` | 救援倒地友方 |
| `capture_pawn` | 俘虏倒地敌人 |
| `ingest_item` | 服食物品 |
| `force_dress` | 强制穿戴衣物 |
| `haul_item` | 搬运物品到目标 |
| `drop_carried` | 放下手中物品 |

### 贸易 (2)
| `list_faction_traders` | 列出可通讯的派系和商船 |
| `trade_execute` | 执行交易 |
| `allow_all_items` | 允许地图所有被禁止的物品 |

### 搜索 (4)
| `search_map` | 按类型搜索地图事物 |
| `find_pawn` | 搜索指定角色/生物 |
| `get_thing_def` | 查询物品定义详情 |
| `search_thing_def` | 按关键词搜索 ThingDef |

### 其他 (10)
| `take_screenshot` | 截取地图画面 |
| `get_structure_layout` | 查看建筑内部布局 |
| `get_construction_status` | 查看建造进度 |
| `move_pawn` | 移动角色到指定坐标 |
| `move_camera` | 移动视角 |
| `get_open_dialogs` | 列出当前弹框 |
| `select_dialog_option` | 选择弹框选项 |
| `get_right_click_menu` | 生成右键菜单 |
| `select_right_click` | 执行右键菜单选项 |
| `submit_feedback` | 向开发者提交反馈 |

### 命令 (8)
| `cancel_task` | 取消殖民者当前/排队任务 |
| `cancel_build` | 取消蓝图/框架/标记 |
| `designate_hunt` | 标记动物狩猎 |
| `designate_slaughter` | 标记动物宰杀 |
| `designate_tame` | 标记动物驯服 |
| `forbid_item` | 禁止区域内物品 |
| `allow_item` | 允许区域内物品 |
| `claim_item` | 占有物品/建筑为玩家派系 |

### 区域/设施 (8)
| `set_bed_owner_type` | 设置床位类型（医疗/囚犯/殖民者） |
| `set_temp_control` | 设置温度控制设备 |
| `list_devices` | 列出/搜索地图设备和可用操作 |
| `get_device_info` | 获取设备状态、组件和 UI/Gizmo 操作 ID |
| `execute_device_action` | 按 action_id 执行设备 UI/Gizmo 或 adapter 操作 |
| `manage_transporter_load` | 查看/清空/取消运输器装载 |
| `list_base_templates` | 列出可用基地模板 |
| `apply_base_template` | 应用基地模板到地图 |

### 腐坏追踪 (2)
| `check_deterioration` | 扫描检查物品腐坏/露天耐久 |
| `get_deteriorating_items` | 腐坏/耐久降低物品清单 |

### 其他 (3)
| `regenerate_map` | 重新生成当前地图（需确认） |
| `set_tool_result_suffix` | 设置工具结果后缀（一次性通知注入） |

## 相关 Mod

- [RimWorld Agent](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261723) — AI 运行时，连接 Claude API 自主管理殖民地
- [RimWorld Agent UI](https://steamcommunity.com/sharedfiles/filedetails/?id=3732261754) — 游戏内 AI 对话窗口
