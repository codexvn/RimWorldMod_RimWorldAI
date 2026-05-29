---
name: trade
description: 派系虚空贸易。在列出可贸易派系、预览货物、执行交易时激活。无需建造通讯台或等待商船，直接与派系进行贸易。
---

# 派系虚空贸易指南

## 前提

**必须有通电的轨道贸易信标**（`Building_OrbitalTradeBeacon`）且交易物品在信标覆盖范围内。否则 `trade_execute` 会失败（无可售物品）。

- 卖出的物品直接消失（转入商船库存后随虚拟商船销毁）
- 买入的物品通过空投舱送达，落到信标附近的地面

## 三步流程

```
1. list_faction_traders()     → 看有哪些派系、商船类型、好感度和折扣
2. trade_execute(faction_name, trader_kind) → 预览该派系当前货物（不传 buy/sell）
3. trade_execute(faction_name, trader_kind, buy/sell) → 执行交易
```

## 缓存机制

虚拟商船按 `派系 + 商船类型` 缓存，**TTL 1 游戏小时**。同一小时内多次调用看到同一批货。

- 先预览、再交易：货物一致
- 超过 1 小时或切换存档后自动刷新
- 用 `trade_execute(faction_name, trader_kind)` 看有哪些商品可买

## 折扣

好感度直接影响交易价格。好感度越高折扣越大：
- 盟友 + 高好感 → 更大折扣
- 中立 + 低好感 → 接近原价
- 敌对无法贸易

## 选择策略

1. **优先卖原材料**：棉布、皮革、石头等堆叠多、价值低的优先卖出清库存
2. **优先买稀缺品**：零件、高级零部件、药物等不可再生资源优先购买
3. **注意头衔限制**：部分商船类型需要皇家头衔（如帝国），`list_faction_traders` 会标注
4. **白银不够时先卖后买**：同一次调用可以同时 sell 和 buy，balance 最终结算
5. **多次交易分批执行**：每种商船有特定库存范围，想要多种物品可以换不同 trader_kind 交易

## 示例

```
# 查看外来者联盟能买什么
trade_execute(faction_name: "外来者联盟", trader_kind: "Bulk Goods")

# 卖出多余的棉布，买入零件
trade_execute(
  faction_name: "外来者联盟",
  trader_kind: "Bulk Goods",
  sell: [{item: "棉布", count: 100}],
  buy: [{item: "零件", count: 5}, {item: "钢铁", count: 200}]
)
```
