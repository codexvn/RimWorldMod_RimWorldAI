# ACP 会话配置级联与游戏内聊天 UI 优化计划

## 状态

已实现，待游戏内回归。

## 配置目录与选择

- 设置页探测遵循 ACP `session/new` / `session/set_config_option` 通用语义，不按 backend 特化。
- 不设置 `set_config_option` 调用次数上限。一次 probe 或新会话中，每个初始/保存的选择最多尝试一次；尚未出现的动态项会在父项应用后重试，因此队列天然终止。
- `session/set_config_option` 的完整响应替换当前配置目录。
- 新出现且没有保存选择的 option 直接采用其 `currentValue` 显示，不自动写入待发送选择，也不再发送一次默认值。
- 用户改动新 option 后才写入 `SessionConfigSelections`；暂未出现的旧选择保留，避免丢失 model 等父项下的动态配置。
- load/resume 成功不覆盖会话配置；仅 `session/new` 应用保存选择。

## 设置页展示

- Session Config 默认折叠，避免每张 backend 卡片在打开设置时被缓存目录撑高。
- “测试连通性并拉取 Session Config”成功后自动展开本卡片。
- 已有缓存目录可手动通过“展开 Session Config”查看；无缓存时只显示测试入口。

## 聊天 UI

- 配置摘要按 ACP 返回顺序完整换行，不截断；Token/上下文信息独立为下一行。
- `ChatDisplayState` 按 revision 缓存 chat、tool、task 快照；流式事件以 15 FPS 合并消费，但不丢事件。
- 消息高度、工具卡片正文/高度和任务排序按内容及宽度缓存。
- ACP `usage_update` 标准只保证当前上下文的 `used` / `size`，不是累计 token。顶部展示真实的上下文百分比与进度条；只有累计账本有实际值时才展示 `Tok`，避免稳定显示误导性的 `Tok 0/...`。

## 运行时问题待修复

### `update_memory` 缺参异常

- 根因：`Tool_UpdateMemory` 对 `section`、`action`、`content` 直接调用 `JsonElement.GetProperty`。缺少任一必填参数会由 `System.Text.Json` 抛出 `KeyNotFoundException`。
- 修复：改为显式验证 object 类型和三个必填字符串；缺参时返回可操作的工具错误结果，不写文件、不抛异常。
- 观测：将内部工具本体执行与“追加模式状态后缀”拆分异常边界；完整异常链写入正式错误日志，避免把后缀生成失败误标为工具失败。
- 验证：补充直接调用 `update_memory` 的缺参、空字符串、正常追加测试。

### PLAN 自动进入与 token 累计

- 当前自动进入 PLAN 的设计路径只有两条：ACP 冷启动首轮（`HasEverSent == false`）和每天 06:00 后的晨报。`enter_act` 本身不会自动回切 PLAN。
- 需在游戏日志中区分上述触发来源，避免 UI/模型把首次 PLAN prompt 误解为执行中回切；是否取消每日晨报自动 PLAN 需由产品策略决定，不能静默改变。
- 累计 token 的 JSON/Scribe 账本已经存在，但只在 ACP prompt response 提供 input/output/cache 数值时调用 `Record`。Codex 当前通过标准 `usage_update` 仅发送本轮上下文 `used/size`，该值不能作为累计消耗落库，否则每轮都会重复累计完整上下文。
- 保持 backend 无特化：若 ACP 标准 prompt response 提供完整 usage，则持久化；若仅提供 `usage_update`，仅展示上下文占用。后续如协议端暴露可累计的标准 usage 字段，再接入账本，而非从上下文值伪造。

## 验证

- Codex 初始 mode/model/reasoning 应用 model 后出现 fast-mode；fast-mode 默认 false 只展示，不额外 set。
- 已保存 fast-mode 在父配置出现后按保存值应用。
- 动态项暂缺时只 warning，不中断会话且不删除设置。
- 构建 RimWorldAgent、RimWorldMCP、RimWorldAgentUI，运行 C# 单测及 Node 协议 smoke。
- 游戏内验证设置折叠、探测自动展开、上下文进度条，以及高频流式输出性能。
