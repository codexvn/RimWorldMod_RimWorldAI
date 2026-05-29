# Tool 系统

## 概述

90+ 个 Tool 将 RimWorld 游戏 API 暴露为 LLM 可调用的 MCP Tool。反射自动注册，命令队列调度到主线程，`GetTargetRange` 接口驱动自动摄像头移动。

## 关键文件

| 文件 | 职责 |
|------|------|
| `Tools/ITool.cs` | Tool 接口定义：`Name`, `Description`, `InputSchema`, `ExecuteAsync`, `GetTargetRange` |
| `Tools/ToolRegistry.cs` | 注册表、执行调度、资源映射、反射自动注册、自动摄像头移动 |
| `McpCommandQueue.cs` | 线程安全命令队列：`ConcurrentQueue<T>` + `TaskCompletionSource` |
| `Tools/CameraHelper.cs` | 摄像头移动、缩放、实体查找辅助方法 |
| `Tools/ResourceCheckHelper.cs` | 建造资源检查辅助工具 |
| `Tools/Tool_*.cs` | 各 Tool 实现（~100 个文件） |

## ITool 接口

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement? args);
    (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args);
}
```

**设计理由**：
- `Name` 和 `Description` 直接暴露给 MCP `tools/list` 响应，LLM 据此选择工具
- `InputSchema` 是 JSON Schema（`System.Text.Json`），描述参数类型和必填项
- `ExecuteAsync` 内部自行 dispatch 到主线程（写操作通过 `McpCommandQueue`）
- `GetTargetRange` 在 HTTP 线程同步调用，返回 `null` 表示无需移动摄像头

## ToolRegistry 初始化与注册

**反射自动发现**：静态构造函数扫描 `typeof(ToolRegistry).Assembly` 中所有 `ITool` 实现，`Activator.CreateInstance` 实例化，读取 `Name` 注册。

**摄像头工具预扫描**：`{"pos_x":0,"pos_y":0}` 探测每个工具的 `GetTargetRange`，非 null 的加入 `s_cameraToolNames` 集合供诊断。`move_camera` 显式跳过。

**设计理由**：反射避免手动维护注册列表。新增工具只需实现 `ITool` 接口即可自动注册。

## 执行流程

```
HTTP Request (transport 线程)
  → McpServer.HandleToolsCallAsync
    → ToolRegistry.ExecuteAsync(name, args)
      → tool.GetTargetRange(args)           ← 同步，HTTP 线程
      → if non-null: CameraHelper.MoveToRange()  ← dispatch 到主线程
      → tool.ExecuteAsync(args)             ← dispatch 到主线程
        → 只读: 直接执行
        → 写操作: McpCommandQueue.DispatchAsync() → 主线程排队
```

**设计理由**：摄像头移动到主线程（`CameraDriver` 只能在主线程用），Tool 执行也在主线程（确保线程安全），但 `GetTargetRange` 在 HTTP 线程同步执行（避免主线程阻塞影响帧率）。摄像头移动先于 Tool 执行，让玩家提前看到操作目标。

## 线程模型

| 操作 | 线程 | 说明 |
|------|------|------|
| JSON 解析 | HTTP 线程 | `System.Text.Json` |
| `GetTargetRange` | HTTP 线程 | 坐标解析 / 实体查找 |
| `CameraHelper.MoveToRange` | 主线程 | 通过 `McpCommandQueue.DispatchAsync` dispatch |
| 只读 Tool 逻辑 | HTTP 线程 | 直接访问 `Find.*`, `DefDatabase<>` 等 |
| 写操作 Tool 逻辑 | 主线程 | 通过 `McpCommandQueue.DispatchAsync` 排队 |

**写操作为什么必须主线程**：RimWorld 的 API（`Designator.DesignateSingleCell()`, `BillStack.AddBill()` 等）非线程安全，必须在 Unity 主线程调用。

## McpCommandQueue

`ConcurrentQueue<McpCommand>` + `TaskCompletionSource` 模式：

```csharp
// 入队：任意线程
var tcs = new TaskCompletionSource<T>();
_queue.Enqueue(new McpCommand { Action = action, Tcs = tcs });
return tcs.Task;

// 出队：主线程每帧 ProcessPending()
while (_queue.TryDequeue(out var cmd))
    cmd.Tcs.SetResult(cmd.Action());
```

**设计理由**：`TaskCompletionSource` 比 `ManualResetEvent` 更轻量，不阻塞线程。HTTP 线程 `await` 等待结果，不占用线程池。

## Tool 开发规范

1. **先查游戏源码**：到 `F:\RiderProjects\Assembly-CSharp\` 追踪完整链路，复用原版 Designator/Job/Bill 逻辑
2. **坐标参数统一左上→右下**：`pos_x/pos_y` → `end_x/end_y`，禁止中心点+半径 API
3. **所有 Tool 实现 GetTargetRange**：坐标类返回矩形范围，ID 类通过 `CameraHelper.FindPawnById/FindThingById` 查找位置，信息查询类返回 `null`
4. **用 thingIDNumber 精确定位**：禁止名称字符串匹配
5. **List 工具必须分页**：数据 >20 条的工具提供 `page`/`page_size` 参数，默认每页 10
6. **到达性检测**：建造类工具默认检查殖民者可达性，不可达到返回错误

详见 `design/camera-system.md`（GetTargetRange 实现模式）。
