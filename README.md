# RimWorld MCP (RimWorldMCP)

RimWorld 1.6 模组——将游戏状态和操作暴露为 MCP (Model Context Protocol) Tool，让 AI 助手（Claude 等）可以直接查询殖民地状况并执行游戏操作。

## 解决的痛点

- **无法远程操控**：原版只能在游戏窗口内操作，离开电脑就无法管理殖民地
- **AI 辅助决策盲区**：LLM 不知道当前资源库存、殖民者状态、研究进度，只能给泛泛建议
- **重复操作繁琐**：批量创建制造单据、调整工作优先级、检查全殖民者健康需要大量点击
- **领域知识门槛**：新手不知何时该造什么装备、科技树怎么走、手术有多大风险

## 功能

- **78 个 MCP Tool**：覆盖通用查询、网格查询、制造管理、建造规划、标记、存储/种植、研究控制、殖民者管理、医疗、战斗、右键操作、搜索、区域管理等 22 大类别
- **真实游戏状态查询**：通过 `Find.*`、`DefDatabase<>` 等 RimWorld API 直接读取殖民地数据
- **反射自动注册**：新增 Tool 只需放在 Tools/ 目录，无需手动注册
- **分页查询**：列表类工具支持 `page`/`page_size` 参数，避免大段返回导致缓存失效
- **写操作线程安全**：所有修改操作通过 `McpCommandQueue` 调度到主线程执行
- **右上菜单操作**：拾取、丢弃、剥除、逮捕、救援、俘虏、服食、强制穿戴、搬运、放下等 10 个基于 Job 系统的右键功能
- **网格可视化**：文本化字符网格地图（64 种符号）和区域详情查询
- **Claude Code 桥接**：通过 WebSocket 连接 CC Companion 进程，支持事件推送（Letter 通知、消息监控、空闲检测、腐坏告警、综合警报、每日早报）和游戏内聊天窗双向通信（Ctrl+Shift+C）
- **物品腐坏追踪**：自动监控地图上腐烂/耐久降低的物品并推送告警
- **OSS 截图上传**：截图自动上传到阿里云 OSS，返回可直接访问的图片 URL
- **领域知识 Skill 系统**：内置 6 个领域知识文件，LLM 可在操作前激活获取最佳实践
- **SSE / Streamable HTTP 双传输**：支持 MCP 2024-11-05 SSE 协议和新版 Streamable HTTP 协议
- **国际简体中文**：工具名称内置简体中文翻译（`publish/Languages/ChineseSimplified/Keyed/RimWorldMCP_Tools.xml`）

## 安全说明

- **凭据明文存档**：BridgeToken、BridgePassword、OssAccessKey、OssSecretKey 通过 RimWorld Scribe 系统以明文形式保存在 Mod 配置文件中（位于 `{SaveData}/Config/Mod_{packageId}_{handleName}.xml`）。任何能读取该文件的人均可获取这些凭据。
- **建议**：使用专用 Token/Key，避免复用重要系统的凭据；定期轮换。
- **设置界面**：当前凭据输入框为明文显示，输入时请注意周围环境。

### Tool 清单

| 类别 | Tool | 说明 |
|------|------|------|
| 通用查询 | `get_game_context` | 殖民地全局状态快照 |
| | `get_resources` | 资源库存报告（分页） |
| | `check_colony` | 殖民地提醒（空闲/崩溃/流血/食物/防御） |
| | `toggle_pause` | 切换暂停 |
| 网格查询 | `get_tile_detail` | 坐标范围详情（建筑/物品/植物/生物） |
| | `get_tile_grid` | 文本化字符网格地图 |
| 制造 | `list_recipes` | 列出可用配方（分页） |
| | `create_production_bill` | 创建制造单据 |
| | `get_bills` | 工作单状态（分页） |
| | `manage_bill` | 暂停/恢复/删除/调整优先级 |
| 建造 | `designate_build` | 放置建造蓝图 |
| | `designate_room` | 快速建造矩形房间 |
| | `uninstall_building` | 拆卸建筑为微缩物品 |
| | `install_minified_thing` | 安装微缩物品 |
| 标记 | `designate_mine` | 标记采矿（矩形范围） |
| | `designate_plants_cut` | 标记砍伐（矩形范围，可过滤树种） |
| | `designate_harvest` | 标记收割（仅成熟作物） |
| | `designate_deconstruct` | 标记拆除（矩形范围） |
| | `designate_clear_plants` | 清除非树木植物 |
| 存储/种植 | `create_stockpile` | 新建储藏区（预设+优先级+筛选） |
| | `create_growing_zone` | 新建种植区 |
| | `set_grower_plant` | 设置种植区作物类型 |
| | `manage_stockpile_filter` | 管理储藏区物品筛选 |
| | `delete_zone` | 删除区域（储藏区/种植区） |
| | `expand_zone` | 扩展区域 |
| 装备 | `find_equipment` | 搜索地图可用武器/衣物 |
| 截图 | `take_screenshot` | 截取画面（支持 OSS 自动上传） |
| 研究 | `list_research_projects` | 列出研究项目（分页） |
| | `get_research_progress` | 研究进度查询 |
| | `set_research_project` | 设置当前研究 |
| | `stop_research` | 停止当前研究 |
| | `get_research_speed` | 研究速度详情 |
| 殖民者管理 | `get_colonists` | 殖民者列表 |
| | `get_colonist_needs` | 详细需求状态 |
| | `get_work_priorities` | 工作优先级表 |
| | `set_work_priority` | 设置工作优先级 |
| | `get_colonist_health` | 健康报告 |
| | `schedule_operation` | 安排手术 |
| 医疗 | `tend_now` | 立即治疗指定殖民者 |
| | `force_surgery` | 强制执行手术 |
| | `get_available_surgeries` | 可用手术列表（分页） |
| 战斗 | `equip_pawn` | 强制装备武器/衣物 |
| | `draft_pawn` | 征召/解除征召 |
| | `get_defense_status` | 防御状态报告 |
| | `attack_pawn` | 攻击指定目标 |
| | `force_attack` | 强制攻击目标（过墙/无视掩体） |
| | `find_enemies` | 搜索地图上的敌人 |
| 右键操作 | `pick_up_item` | 拾取物品 |
| | `drop_equipment` | 丢弃装备 |
| | `strip_pawn` | 剥除衣物/装备 |
| | `arrest_pawn` | 逮捕 |
| | `rescue_pawn` | 救援倒地友方 |
| | `capture_pawn` | 俘虏倒地敌人 |
| | `ingest_item` | 服食 |
| | `force_dress` | 强制穿戴 |
| | `haul_item` | 搬运物品到指定位置 |
| | `drop_carried` | 放下手中物品 |
| 全局操作 | `allow_all_items` | 允许所有被禁止的物品 |
| 搜索 | `search_map` | 按类型搜索地图上的事物（分页） |
| | `find_pawn` | 搜索指定角色/生物（分页） |
| | `get_thing_def` | 查询物品定义详情 |
| | `search_thing_def` | 按关键词搜索 ThingDef（分页） |
| 建筑布局 | `get_structure_layout` | 查看建筑物内部布局 |
| | `get_construction_status` | 查看建造进度 |
| 移动 | `move_pawn` | 移动角色到指定坐标 |
| | `move_camera` | 移动视角到指定坐标 |
| 弹框 | `get_open_dialogs` | 获取当前弹框列表 |
| | `select_dialog_option` | 选择弹框选项 |
| 区域管理 | `set_bed_owner_type` | 设置床位类型（医疗/囚犯/殖民者） |
| | `set_temp_control` | 设置温度控制（冷暖/温度目标） |
| Skill | `get_skills` | 列出可用领域知识 |
| | `active_skill` | 加载知识库内容 |
| TODO | `todo_add` | 添加待办任务 |
| | `todo_delete` | 删除待办任务 |
| | `todo_query` | 查询待办任务 |
| 基地模板 | `list_base_templates` | 列出可用基地模板 |
| | `apply_base_template` | 应用基地模板 |
| 反馈 | `submit_feedback` | 向开发者提交反馈 |
| 腐坏追踪 | `get_deteriorating_items` | 腐坏物品清单 |
| 地图 | `regenerate_map` | 重新生成当前地图（i_know_danger 确认） |

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
├── GameComponent_McpServer.cs         # GameComponent，管理 MCP 服务生命周期（反射注册 Tool）
├── McpCommandQueue.cs                 # 线程安全命令队列
├── Transport/                         # 传输层 (SSE, Streamable HTTP, stdio)
├── Mcp/                               # MCP 协议层 (JSON-RPC 调度)
├── Tools/                             # 78 个 Tool 实现 + 注册表 + 辅助工具
├── cc-companion/                      # Claude Code 伴随进程（TypeScript, tsx 运行时）
├── Bridge/                            # CC 桥接生命周期 + WebSocket 客户端 + 事件转发
├── Skills/                            # 6 个领域知识 .md 文件 + 加载器
├── McpModSettings.cs                  # Mod 设置
├── McpLog.cs                          # 统一日志
├── McpOssUploader.cs                  # 阿里云 OSS 上传
├── publish/Languages/                 # 简体中文工具名称翻译
└── About/                             # Mod 元数据
```

- **net472 Library**：与 RimWorld Unity 运行时一致
- **零 NuGet 业务依赖**：仅 `System.Text.Json` 用于 JSON 序列化
- **参考 Assembly-CSharp**：Tool 直接调用 `Find.*`、`DefDatabase<>` 等游戏 API
