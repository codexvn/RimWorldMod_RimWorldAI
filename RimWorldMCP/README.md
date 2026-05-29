# RimWorld MCP

RimWorld 1.6 模组——将游戏状态和操作暴露为 MCP (Model Context Protocol) Tool，让 AI 助手（Claude 等）可以直接查询殖民地状况并执行游戏操作。

## 解决的痛点

- **无法远程操控**：原版只能在游戏窗口内操作，离开电脑就无法管理殖民地
- **AI 辅助决策盲区**：LLM 不知道当前资源库存、殖民者状态、研究进度，只能给泛泛建议
- **重复操作繁琐**：批量创建制造单据、调整工作优先级、检查全殖民者健康需要大量点击
- **领域知识门槛**：新手不知何时该造什么装备、科技树怎么走、手术有多大风险

## 功能

- **100+ 个 MCP Tool**：覆盖通用查询、网格查询、制造管理、建造规划、标记、存储/种植、研究控制、殖民者管理、医疗、战斗、右键操作、搜索、任务管理、区域管理、记忆系统等类别
- **真实游戏状态查询**：通过 `Find.*`、`DefDatabase<>` 等 RimWorld API 直接读取殖民地数据
- **反射自动注册**：新增 Tool 只需放在 Tools/ 目录，无需手动注册
- **分页查询**：列表类工具支持 `page`/`page_size` 参数，避免大段返回导致缓存失效
- **写操作线程安全**：所有修改操作通过 `McpCommandQueue` 调度到主线程执行
- **游戏内聊天窗**：双栏布局（对话流 + 工具调用记录 + TODO），流式思考/正文显示，AI 思考中标签，工具执行耗时
- **Claude Code 桥接**：WebSocket 连接 CC Companion 进程，事件推送（袭击/死亡/负面通知/腐坏告警/空闲兜底/每日早报）+ 自动暂停
- **Token 预算系统**：按存档限制 Token 消耗，Block/Warn 模式 + Webhook 回调
- **物品腐坏追踪**：自动监控腐烂/耐久降低物品
- **OSS 截图上传**：截图自动上传阿里云 OSS，返回图片 URL
- **任务系统**：查询任务 (`list_quests`)、接受任务 (`accept_quest`)
- **领域知识 Skill 系统**：11 个领域知识（基地建造、殖民地管理、战斗准备、装备制造/优化、医疗、角色交互、研究、资源物流、贸易、任务管理）
- **SSE / Streamable HTTP 双传输**
- **简体中文**：工具名称内置中文翻译

## Tool 清单

| 类别 | Tool | 说明 |
|------|------|------|
| 通用查询 | `get_game_context` | 殖民地全局状态快照 |
| | `get_resources` | 资源库存报告 |
| | `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） |
| | `toggle_pause` | 切换暂停 |
| | `advance_tick` | 推进指定 tick 后暂停返回 |
| 网格查询 | `get_tile_detail` | 坐标范围详情（建筑/物品/植物/生物） |
| | `get_tile_grid` | 文本化字符网格地图（64 种符号） |
| | `fertility_grid` | 地面肥沃度视图 |
| | `terrain_grid` | 地形类型视图 |
| | `temperature_grid` | 温度分布视图 |
| | `pollution_grid` | 污染程度视图 |
| 制造 | `list_recipes` | 可用配方（分页） |
| | `create_production_bill` | 创建制造单据 |
| | `get_bills` | 工作单状态（分页） |
| | `manage_bill` | 暂停/恢复/删除/调整优先级 |
| 建造 | `designate_build` | 放置建造蓝图 |
| | `designate_room` | 快速建造矩形房间 |
| | `uninstall_building` | 拆卸建筑为微缩物品 |
| | `install_minified_thing` | 安装微缩物品 |
| 标记 | `designate_mine` | 标记采矿（矩形范围） |
| | `designate_plants_cut` | 标记砍伐（可过滤树种） |
| | `designate_harvest` | 标记收割（仅成熟作物） |
| | `designate_deconstruct` | 标记拆除 |
| | `designate_clear_plants` | 清除非树木植物 |
| 命令 | `designate_hunt` | 标记狩猎 |
| | `designate_slaughter` | 标记宰杀 |
| | `designate_tame` | 标记驯服 |
| | `forbid_item` | 禁止区域内物品 |
| | `allow_item` | 允许区域内物品 |
| | `claim_item` | 占有区域内物品/建筑 |
| 存储/种植 | `create_stockpile` | 新建储藏区 |
| | `create_growing_zone` | 新建种植区 |
| | `set_grower_plant` | 设置种植区作物 |
| | `manage_stockpile_filter` | 管理储藏区筛选 |
| | `delete_zone` | 删除区域 |
| | `expand_zone` | 扩展区域 |
| 装备 | `find_equipment` | 搜索地图可用武器/衣物 |
| | `get_recommended_apparel` | 按游戏评分推荐衣物 |
| | `get_recommended_weapon` | 按科技等级推荐武器 |
| 截图 | `take_screenshot` | 截取画面（OSS 自动上传） |
| 研究 | `list_research_projects` | 研究项目（分页） |
| | `get_research_progress` | 研究进度 |
| | `set_research_project` | 设置当前研究 |
| | `stop_research` | 停止研究 |
| | `get_research_speed` | 研究速度详情 |
| 殖民者管理 | `get_colonists` | 殖民者列表 |
| | `get_colonist_needs` | 详细需求状态 |
| | `get_work_priorities` | 工作优先级表 |
| | `set_work_priority` | 设置工作优先级 |
| | `get_colonist_health` | 健康报告 |
| | `schedule_operation` | 安排手术 |
| 医疗 | `tend_now` | 立即治疗 |
| | `force_surgery` | 强制执行手术 |
| | `get_available_surgeries` | 可用手术列表（分页） |
| 战斗 | `equip_pawn` | 强制装备武器/衣物 |
| | `draft_pawn` | 征召/解除征召 |
| | `get_defense_status` | 防御状态报告 |
| | `attack_pawn` | 攻击目标 |
| | `force_attack` | 强制攻击（过墙/无视掩体） |
| | `find_enemies` | 搜索敌人 |
| 右键操作 | `pick_up_item` | 拾取物品 |
| | `drop_equipment` | 丢弃装备 |
| | `strip_pawn` | 剥除衣物/装备 |
| | `arrest_pawn` | 逮捕 |
| | `rescue_pawn` | 救援倒地友方 |
| | `capture_pawn` | 俘虏倒地敌人 |
| | `ingest_item` | 服食 |
| | `force_dress` | 强制穿戴 |
| | `haul_item` | 搬运物品 |
| | `drop_carried` | 放下物品 |
| 全局操作 | `allow_all_items` | 允许所有被禁止物品 |
| 搜索 | `search_map` | 按类型搜索地图事物（分页） |
| | `find_pawn` | 搜索角色/生物（分页） |
| | `get_thing_def` | 查询物品定义 |
| | `search_thing_def` | 按关键词搜索 ThingDef（分页） |
| 建筑布局 | `get_structure_layout` | 建筑物内部布局 |
| | `get_construction_status` | 建造进度 |
| 移动 | `move_pawn` | 移动角色到指定坐标 |
| | `move_camera` | 移动视角到指定坐标 |
| 弹框 | `get_open_dialogs` | 当前弹框列表 |
| | `select_dialog_option` | 选择弹框选项 |
| 任务 | `list_quests` | 任务列表（分页，按状态过滤） |
| | `accept_quest` | 接受任务 |
| 区域管理 | `set_bed_owner_type` | 设置床位类型 |
| | `set_temp_control` | 设置温度控制 |
| Skill | `get_skills` | 列出可用领域知识 |
| | `active_skill` | 激活加载知识内容 |
| TODO | `todo_add` | 添加待办 |
| | `todo_delete` | 删除待办 |
| | `todo_query` | 查询待办 |
| 基地模板 | `list_base_templates` | 列出可用模板 |
| | `apply_base_template` | 应用模板 |
| 反馈 | `submit_feedback` | 提交反馈 |
| 腐坏追踪 | `get_deteriorating_items` | 腐坏物品清单 |
| 地图 | `regenerate_map` | 重新生成地图 |
| 记忆 | `add_memory` | 添加记忆（JSON 持久化） |
| | `list_memories` | 列出所有记忆 |
| | `delete_memory` | 删除记忆 |
| | `update_memory` | 更新记忆优先级/内容 |

## Claude Desktop 配置

游戏启动后 MCP 服务自动运行在 `http://localhost:9877`。

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

```bash
dotnet build
```

输出：`publish/1.6/Assemblies/RimWorldMCP.dll`

推荐创建目录链接到 RimWorld mod 目录：

```
mklink /D F:\SteamLibrary\steamapps\common\RimWorld\Mods\RimWorldMCP F:\RiderProjects\RimWorldMCP\publish
```

## 项目结构

```
RimWorldMCP/
├── RimWorldMCPMod.cs                  # Mod 入口，管理设置窗口
├── GameComponent_McpServer.cs         # GameComponent，管理 MCP 服务生命周期
├── McpCommandQueue.cs                 # 线程安全命令队列
├── Transport/                         # 传输层 (SSE, Streamable HTTP)
├── Mcp/                               # MCP 协议层 (JSON-RPC 调度)
├── Tools/                             # 100+ 个 Tool 实现 + 注册表 + MemoryManager
├── Bridge/                            # CC 桥接（生命周期/WS/事件/Token 追踪）
├── Skills/                            # 11 个领域知识 .md + 加载器
├── UI/                                # 游戏内聊天窗（双栏布局、流式显示）
├── McpModSettings.cs                  # Mod 设置
├── McpLog.cs                          # 统一日志
├── McpOssUploader.cs                  # 阿里云 OSS 上传
├── cc-companion/                      # CC Companion (TypeScript, tsx 运行时)
├── publish/Languages/                 # 简体中文工具名称翻译
├── publish/cc-companion/              # Companion 源码副本
└── About/                             # Mod 元数据
```

- **net472 Library**：与 RimWorld Unity 运行时一致
- **NuGet 依赖**：`System.Text.Json` 8.0.5 + Aliyun OSS SDK
- **引用 Assembly-CSharp**：Tool 直接调用 `Find.*`、`DefDatabase<>` 等游戏 API
