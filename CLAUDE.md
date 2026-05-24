# RimWorldMCP

MCP (Model Context Protocol) 服务器，将 RimWorld 游戏状态和操作暴露为 LLM 可调用的 Tool。作为 RimWorld mod DLL 内嵌运行。

**游戏源码**: `F:\RiderProjects\Assembly-CSharp\`（RimWorld 反编译 C# 源码，net472 Class Library）

## 项目结构

```
RimWorldMCP/
├── GameComponent_McpServer.cs            # Mod 入口：GameComponent 子类，管理 MCP 服务生命周期
├── McpCommandQueue.cs                    # 线程安全命令队列（ConcurrentQueue + TaskCompletionSource）
├── Transport/                            # 传输层
│   ├── ITransport.cs                     # 抽象接口
│   ├── SseTransport.cs                   # GET /sse + POST /message（HttpListener 后台线程）
│   ├── StreamableHttpTransport.cs        # POST /mcp（新版协议）
│   └── StdioTransport.cs                 # stdin/stdout（保留，不在游戏内使用）
├── Mcp/                                  # MCP 协议层
│   ├── McpServer.cs                      # JSON-RPC 调度：initialize/tools/list/tools/call/resources
│   └── McpMessage.cs                     # 数据类型：请求/响应/Tool定义/资源
├── Tools/                                # 24 个 Tool（真实 RimWorld API 调用）
│   ├── ITool.cs                          # Tool 接口 + ToolResult
│   ├── ToolRegistry.cs                   # 注册表 + 执行调度 + 资源映射
│   └── Tool_*.cs                         # 各 Tool 实现
├── Skills/                               # 领域知识 Skill 系统
│   ├── SkillInfo.cs                      # Skill 数据模型
│   ├── SkillRegistry.cs                  # 加载 .md 文件、解析 frontmatter
│   └── *.md                              # 6 个 Skill 文件
└── About/
    └── About.xml                         # Mod 元数据
```

## 架构

单进程 mod 内嵌——LLM 通过 SSE 或 Streamable HTTP 连接游戏内的 MCP 服务。

- **net472 Library**：与 RimWorld Unity 运行时一致，`OutputType=Library`
- **引用 Assembly-CSharp.dll**：Tool 直接调用游戏 API（`Find.*`、`DefDatabase<>`、`PawnsFinder` 等）
- **GameComponent 入口**：反射自动发现，`StartedNewGame()` 时启动 HttpListener
- **线程安全**：只读 Tool 在 HttpListener 线程直接执行；写操作 Tool 通过 `McpCommandQueue` 调度到主线程
- **NuGet**: 仅 `System.Text.Json` 8.0.5（JSON 序列化）
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

## Tool 清单（25 个，真实 API）

### 通用查询
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_game_context` | 游戏全局状态快照 | `Find.CurrentMap`, `Find.TickManager`, `Find.ResearchManager` |
| `get_resources` | 资源库存报告 | `map.resourceCounter.AllCountedAmounts` |

### 制造 (4)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_recipes` | 列出可用配方 | `DefDatabase<RecipeDef>.AllDefs` |
| `create_production_bill` | 创建制造单据 | `BillStack.AddBill()` (入队) |
| `get_bills` | 查看工作单状态 | `map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>()` |
| `manage_bill` | 管理单据（暂停/恢复/删除/优先级） | `bill.suspended`, `billStack.Delete()`, `billStack.Reorder()` (入队) |

### 建造 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_build` | 放置建造蓝图（参数: pos_x=水平, pos_y=垂直网格） | `GenConstruct.PlaceBlueprintForBuild()` (入队) |
| `designate_room` | 快速建造矩形房间（参数: center_x=水平, center_y=垂直网格） | 批量 `PlaceBlueprintForBuild()` (入队) |

### 伐木与采矿 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `designate_plants_cut` | 标记植物砍伐（pos_x/y=起点, end_x/y=终点可选, 支持矩形范围） | `DesignationManager.AddDesignation(CutPlant)` (入队) |
| `designate_mine` | 标记采矿（pos_x/y=起点, end_x/y=终点可选, 支持矩形范围） | `DesignationManager.AddDesignation(Mine)` (入队) |

### 截图 (1)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `take_screenshot` | 截取地图指定 X/Z 范围画面（参数: min_x/max_x 必填, min_z/max_z 可选） | `ScreenshotTaker.TakeNonSteamShot()` (入队) |

### 研究 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `list_research_projects` | 列出研究项目 | `DefDatabase<ResearchProjectDef>.AllDefsListForReading` |
| `get_research_progress` | 获取研究进度 | `Find.ResearchManager.GetProgress()` |
| `set_research_project` | 设置研究项目 | `Find.ResearchManager.SetCurrentProject()` (入队) |

### 殖民者需求 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonists` | 殖民者信息 | `PawnsFinder.AllMaps_FreeColonistsSpawned` |
| `get_colonist_needs` | 详细需求状态 | `pawn.needs.AllNeeds` |
| `set_work_priority` | 设置工作优先级 | `pawn.workSettings.SetPriority()` (入队) |

### 医疗 (2)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `get_colonist_health` | 健康报告 | `pawn.health.hediffSet.hediffs` |
| `schedule_operation` | 安排手术 | `billStack.AddBill(Bill_Medical)` (入队) |

### 战斗 (3)
| Tool | 说明 | 数据源/操作 |
|------|------|------------|
| `equip_pawn` | 装备武器/衣物 | `pawn.equipment.AddEquipment()` / `pawn.apparel.Wear()` (入队) |
| `draft_pawn` | 征召/解除征召 | `pawn.drafter.Drafted` (入队) |
| `get_defense_status` | 防御状态报告 | `pawn.equipment.Primary`, `map.listerBuildings` |

### Skill (2)
| Tool | 说明 | 数据源 |
|------|------|--------|
| `get_skills` | 列出可用领域知识 | `SkillRegistry.GetAll()` |
| `active_skill` | 激活获取 Skill 内容 | `SkillRegistry.Get(name)` |

## Skill 系统

Skill 是领域知识文件（Markdown + YAML frontmatter），存放在 `Skills/` 目录。

| Skill | 内容 |
|-------|------|
| `equipment-crafting` | 装备制造策略、品质控制、材料选择 |
| `colony-management` | 殖民地管理、工作分配、资源规划 |
| `combat-preparation` | 战斗准备、阵地部署、武器射程 |
| `base-building` | 基地布局设计、13x13 标准间、材料选择 |
| `research-management` | 科技树优先级、研究资源配置 |
| `medical-care` | 手术风险分析、植入体策略、药物使用 |

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

- net472，仅 `System.Text.Json` NuGet 依赖
- 日志输出到 `Console.Error`（Transport 层）和 `Verse.Log`（GameComponent 层）
- Tool 返回值：`ToolResult` → `McpServer` 包装为 `{"content":[{"type":"text","text":"..."}]}`
- 写操作必须通过 `McpCommandQueue` 调度到主线程
- `dotnet build` → `publish/1.6/Assemblies/RimWorldMCP.dll`
- **坐标陷阱**：`IntVec3(x,y,z)` 中 `y` 是海拔，`z` 是网格垂直轴。MCP 用户的 `pos_y` 必须映射到 `IntVec3.z`，写 `new IntVec3(x, posY, 0)` 是 bug
- **HttpListener 陷阱**：`StartAsync` 中 `HttpListener.Start()` 可能抛 `HttpListenerException`（端口占用/权限不足），需提供中文诊断；`_transport` 要在 `StartAsync` 成功后才赋值；RimWorld 返回主菜单会导致 Game 对象被 Dispose 但 GameComponent 不通知，需静态字段跨实例清理
