# 分块地图渲染与压缩系统

## 概述

将 5 个网格工具的查询模式从"矩形范围返回原始字符网格"改为"chunk_id → 单 Chunk + 压缩"，降低 Token 消耗并提升 LLM prompt cache 命中率。

## LLM 工作流

```
list_chunks(pos_x=10, pos_y=20, end_x=90, end_y=80)
→ Chunk(0,0), Chunk(1,0), Chunk(2,0), Chunk(0,1), ...
→ chunk_id 列表: "0_0", "1_0", "2_0", "0_1", ...

get_tile_grid(chunk_id="0_0")  →  Chunk(0,0) 内容
get_tile_grid(chunk_id="1_0")  →  Chunk(1,0) 内容
...（可并行调用，每调用独立缓存）
```

## 架构

```
list_chunks (矩形→chunk_id列表)
  pos_x, pos_y, end_x?, end_y?
  → MapChunker.GetIntersectingChunks()
  → 行主序 Chunk 列表 + chunk_id 列表

Grid Tool (chunk_id → 单 Chunk)
  chunk_id "X_Z"
  → MapChunker.TryParseChunkId() → GetChunkByIndex()
  → CellCharProviders (单元格→字符，查 SymbolDictionary)
  → IChunkCompressor (未压缩 / RLE / RowRef+RLE)
  → 输出: 头部 + 压缩数据 + 图例
```

## 文件职责

| 文件 | 职责 |
|------|------|
| `McpModSettings.cs` | `CompressionMethod` 枚举、`ChunkWidth`/`ChunkHeight`/`GridCompression` 设置项 |
| `RimWorldMCPMod.cs` | 设置窗口：分块尺寸/压缩方法切换 |
| `Compression/IChunkCompressor.cs` | 压缩接口：双重载 `Compress(char[][], chunkIndex)`（heatmap）+ `Compress(GridData, chunkIndex)`（多层） |
| `Compression/UncompressedCompressor.cs` | 未压缩：`L00=chars...` |
| `Compression/RleCompressor.cs` | RLE：`L00=#1.30#1`，十进制 count |
| `Compression/RowRefCompressor.cs` | RowRef+RLE：`*L{row}` 同行去重引用 |
| `Compression/CompressorFactory.cs` | 工厂：`Create(method) → IChunkCompressor` |
| `MapRendering/MapChunk.cs` | 分块数据模型：XIndex/ZIndex, Bounds, CompressedData |
| `MapRendering/MapChunker.cs` | `GetChunkByIndex()` / `TryParseChunkId()` / `FormatChunkId()` / `GetIntersectingChunks()` |
| `MapRendering/SymbolDictionary.cs` | 词表驱动（Symbols.json symbols 字段）+ 兜底池分配（fallback_pool），每次启动重建 |
| `MapRendering/CellSerializer.cs` | 多层单元格→文本序列化（base 层合并 / `[...]` 触发规则），脱离数据模型 |
| `MapRendering/GridData.cs` | 多层单元格数据模型（row-major）+ CellData/CellLayer 结构 |
| `MapRendering/GridRenderer.cs` | 矩形遍历双管线：char 版（heatmap）/ CellData 版（get_tile_grid） |
| `MapRendering/CellCharProviders.cs` | 单元格映射函数：ForTileGrid→CellData（多层）/ For{Terrain,Fertility,Temperature,Pollution}→char（单值） |
| `Tools/Tool_ListChunks.cs` | 矩形范围 → chunk_id 列表 |
| `Tools/Tool_GetSymbolDictionary.cs` | 字典查询：all/forward/reverse/by_chars |
| `Tools/Tool_GetTileGrid.cs` | chunk_id → 综合网格 |
| `Tools/Tool_TerrainGrid.cs` | chunk_id → 地形网格 |
| `Tools/Tool_FertilityGrid.cs` | chunk_id → 肥沃度网格 |
| `Tools/Tool_TemperatureGrid.cs` | chunk_id → 温度网格 |
| `Tools/Tool_PollutionGrid.cs` | chunk_id → 污染网格 |
| `GameComponent_McpServer.cs` | 启动时 `SymbolDictionary.Initialize()` |

## 压缩协议

### RLE（默认）

连续相同字符合并为 `{字符}{十进制次数}`。网格字符不含 `0-9`（全为 Unicode 符号），十进制数字无二义性——读到字符后，后续数字即为其重复次数，遇非数字停止。

```
L00=#32
L01=#1.30#1
L02=#1.5B2.6S4.10#1
```

解读：`#1.30#1` → `#` ×1 + `.` ×30 + `#` ×1

## list_chunks 工具

### 入参

```
pos_x, pos_y   — 左下角（必填）
end_x, end_y   — 右上角（可选，默认=pos）
```

### 输出

```
## 矩形 (10,20)~(55,60) 覆盖 Chunk 列表
## 分块: 32x32  地图: 250x250  分块网格: 8x8  行主序

Chunk(0,0) (0,0)-(31,31)
Chunk(1,0) (32,0)-(63,31)
Chunk(0,1) (0,32)-(31,63)
Chunk(1,1) (32,32)-(63,63)

## chunk_id 列表
0_0
1_0
0_1
1_1
```

GetTargetRange：返回矩形本身的世界边界。

## 网格工具输出格式

```
## get_tile_grid  Chunk(2,1)  世界(64,32)-(95,63)  [32x32]
## 压缩: RLE  字典Hash: a3f2b1c0

L00=#32
L01=#1.14D1.14#1
...

## 图例
#=墙  .=空地  B=床  D=门
```

- 头部含 chunk 索引 + 世界坐标 + 尺寸 — 同 chunk_id 同格式，缓存友好
- 字典 Hash 供 LLM 判断是否需要刷新字典

## 符号字典

### 策略：固定映射 + 动态分配

固定映射覆盖 ~150 个常用 Def（墙 `#`、门 `D`、床 `◻` 等），使用直观字符。冷门及 Mod 新增 Def 从 Unicode 多级池按 defName 字母序动态分配。

### Initialize() 流程

1. 加载 JSON → 校验 mod 集 Hash → 匹配则直接使用
2. 不匹配则重建：遍历 DefDatabase → 固定表优先匹配 → 剩余按字母序从 Unicode 池分配 → Save()

### 符号池

| 优先级 | 池 | 累计 |
|--------|-----|------|
| 固定 | 专用字符（`#`, `D`, `◻`, `☈` 等 ~35） | 35 |
| 动态1 | `a-z` 剩余 | ~50 |
| 动态2 | `α-ω` 希腊字母 24 | ~74 |
| 动态3 | 方块/数学/特殊符号 | 100+ |

### 持久化

JSON 文件：`Application.persistentDataPath/RimWorldMCP_SymbolDictionary.json`

### 字典 Hash

所有 Def 的 defName 排序后拼接字符串长度，转 8 位 hex。mod 增删必然改变长度。

## GetTargetRange 规范化

| 工具类型 | GetTargetRange 返回值 |
|---------|---------------------|
| chunk_id 网格工具 | chunk 的世界边界（`GetChunkByIndex`） |
| list_chunks | 矩形本身的世界边界 |
| get_symbol_dictionary | `null`（信息查询，无需移动摄像头） |

## 设计决策

1. **chunk_id 直接暴露**：`"0_0"` 是稳定标识符，LLM 可直接按需查询，缓存命中率最高
2. **list_chunks 解耦**：LLM 先获取 chunk 列表，再并行查询各 chunk，Token 消耗可精确预估
3. **压缩在服务端**：MCP 单向，LLM 根据格式说明自行解析
4. **十进制 count**：网格字符无 `0-9`，`{char}{十进制}` 无二义性，无需分隔符
5. **RowRef 不跨 Chunk**：每个 Chunk 自包含
6. **固定映射 + 动态分配**：常用 Def 直观可读，冷门 Def 自动扩展
7. **默认 32x32**：2 的幂，与 RimWorld `Chunks` 二分惯例一致
