"""
 generate_symbols.py

 从 RimWorld 游戏 XML Def 中提取所有 ThingDef/TerrainDef，为每个 def 分配独立 Unicode 字符，
 生成 Symbols.json 词表文件。

 用法:
   python3 generate_symbols.py [RimWorld安装目录]

   默认: F:/SteamLibrary/steamapps/common/RimWorld

 输出:
   RimWorldMCP/resource/Symbols.json

 ## 工作流程

 1. 扫描所有 DLC 的 Defs/ 目录，收集 <ThingDef> 和 <TerrainDef>
 2. 跳过抽象定义（isAbstract=true）
 3. 按 defName 字母序从 Unicode 符号池分配独立字符
 4. 维护一对一映射：一个字符 = 一个 def

 ## 生成后的手动润色

 脚本分配的字符是纯机械的 —— 按字母序依次从池中取，没有语义关联。
 生成后应由 AI 对重要 def 的字符进行语义优化，例如:

   "Wall":     { "char": "㆛" }  → "char": "#"    墙的语义
   "Door":     { "char": "㆝" }  → "char": "D"    门的语义
   "WaterDeep": { "char": "㇇" }  → "char": "〰"   深水的语义
   "Soil":     { "char": "㇍" }  → "char": "."    土地的语义

 原则:
   - 保持一对一（改之前先确认目标字符未被占用）
   - 优先用 ASCII 和常见符号
   - 字符与 def 语义尽量匹配
   - fertility/temperature/pollution 网格使用的字符不要占用: ▓ ▒ ░ · ○ ◎ ● █ P .

 ## 缓存刷新

 修改 Symbols.json 后，删除 RimWorld 持久化目录下的缓存文件使生效:
   {persistentDataPath}/RimWorldMCP_SymbolDictionary.json
"""

import os
import json
import re
import sys
import unicodedata

# ---- 配置 ----

RIMWORLD_ROOT = sys.argv[1] if len(sys.argv) > 1 else "F:/SteamLibrary/steamapps/common/RimWorld"
DLC_LIST = ["Core", "Ideology", "Royalty", "Biotech", "Anomaly"]
OUTPUT_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "resource", "Symbols.json")

# 这些符号被 fertility/temperature/pollution 固定网格占用，不能分配给词表
RESERVED_CHARS = set("▓▒░·○◎●█P.?")


# ---- Def 分类 ----

def classify(def_name: str, tag: str, parent_dir: str) -> str:
    """根据 XML tag 和所在子目录名推断 def 分类"""
    if tag == "TerrainDef":
        return "Terrain"

    p = parent_dir.lower()

    # 建筑相关目录
    building_keywords = [
        "building", "furniture", "production", "power", "security",
        "structure", "temperature", "art", "joy", "mech", "exotic",
        "special", "musical", "natural", "ideo", "ritual", "deathrest",
        "recharger", "fleshmass", "obelisk", "psychic", "void", "ship",
        "cult", "condition", "ancient",
    ]
    if any(kw in p for kw in building_keywords):
        return "Building"

    # 植物
    if "plant" in p:
        return "Plant"

    # 生物
    pawn_keywords = ["race", "animal", "human", "insect", "mechanoid", "entity", "fleshbeast"]
    if any(kw in p for kw in pawn_keywords):
        return "Pawn"

    # 其余 ThingDef 归为物品
    return "Item"


# ---- Unicode 符号池 ----

def build_pool() -> list[str]:
    """
    从多个 Unicode 区块收集可打印字符，按区块顺序排列。
    优先选择等宽渲染友好的区间: CJK相关、几何图形、技术符号等。
    最终去重保序，确保池中无重复。
    """
    # 按优先级排列的 Unicode 区块
    ranges = [
        (0x3190, 0x319F),   # 汉文训读标记 — 小号方框内字符
        (0x31C0, 0x31EF),   # CJK 笔画
        (0x3200, 0x32FF),   # 带圈/括号 CJK — 256 个等宽字符
        (0x3300, 0x33FF),   # CJK 兼容方块
        (0x2300, 0x23FF),   # 杂项技术符号
        (0x25A0, 0x25FF),   # 几何图形 — 方块/三角/圆形
        (0x2600, 0x26FF),   # 杂项符号 — 星形/天气/棋子
        (0x2700, 0x27BF),   # 装饰符号 — 剪刀/星标
        (0x27C0, 0x27EF),   # 数学符号 A
        (0x2980, 0x29FF),   # 数学符号 B
        (0x2B00, 0x2BFF),   # 补充箭头/图形
        (0x00C0, 0x024F),   # 拉丁扩展 — 带变音符字母
        (0x0370, 0x03FF),   # 希腊/科普特
        (0x0400, 0x04FF),   # 西里尔
        (0x0530, 0x06FF),   # 亚美尼亚/希伯来/阿拉伯
        (0x0E00, 0x0E7F),   # 泰文
        (0x2500, 0x257F),   # 制表符 — 线条/边框
        (0x2580, 0x259F),   # 方块元素
        (0x2800, 0x28FF),   # 盲文 — 256 个均匀图案
        (0xFF00, 0xFFEF),   # 半角/全角形式
    ]

    pool = []
    for lo, hi in ranges:
        for cp in range(lo, hi + 1):
            c = chr(cp)
            cat = unicodedata.category(c)

            # 跳过控制字符、空白、代理区、私有区
            if cat.startswith("C") or cat in ("Zs", "Zl", "Zp"):
                continue
            # 跳过被固定网格占用的字符
            if c in RESERVED_CHARS:
                continue

            pool.append(c)

    # 去重保序 — 同一个 Unicode 码点可能落在多个区间的重叠处
    seen = set()
    unique = []
    for c in pool:
        if c not in seen:
            seen.add(c)
            unique.append(c)

    return unique


# ---- XML 解析 ----

def extract_defs() -> dict[str, dict]:
    """
    遍历所有 DLC 的 Defs/*.xml，提取 ThingDef 和 TerrainDef。
    跳过 isAbstract=true 的模板定义。

    返回: {defName: {"label": str, "group": str}, ...}
    """
    defs = {}

    for dlc in DLC_LIST:
        defs_dir = os.path.join(RIMWORLD_ROOT, "Data", dlc, "Defs")
        if not os.path.isdir(defs_dir):
            continue

        for root, _, files in os.walk(defs_dir):
            for fname in files:
                if not fname.endswith(".xml"):
                    continue
                path = os.path.join(root, fname)
                try:
                    content = open(path, encoding="utf-8").read()
                except Exception:
                    continue

                # 匹配 ThingDef/TerrainDef 之间的内容（含嵌套的 Def 时不误匹配）
                blocks = re.findall(
                    r"<(ThingDef|TerrainDef)[^>]*>(.*?)</(ThingDef|TerrainDef)>",
                    content, re.DOTALL,
                )
                for tag_open, block, tag_close in blocks:
                    if tag_open != tag_close:
                        continue

                    # 跳过抽象模板
                    if re.search(r"<isAbstract>\s*true\s*</isAbstract>", block, re.IGNORECASE):
                        continue

                    m_name = re.search(r"<defName>(.*?)</defName>", block)
                    if not m_name:
                        continue
                    name = m_name.group(1)

                    m_label = re.search(r"<label>(.*?)</label>", block)
                    label = m_label.group(1) if m_label else name

                    parent_dir = os.path.basename(os.path.dirname(path))
                    cat = classify(name, tag_open, parent_dir)

                    if name not in defs:
                        defs[name] = {"label": label, "group": cat}

    return defs


# ---- 主流程 ----

def main():
    print(f"扫描 RimWorld: {RIMWORLD_ROOT}")

    # 1. 抽取所有 Def
    defs = extract_defs()
    print(f"Def 总数: {len(defs)}")

    # 2. 构建符号池
    pool = build_pool()
    print(f"符号池大小: {len(pool)}")

    # 3. 按 defName 字母序分配，保证每次生成结果一致
    sorted_names = sorted(defs.keys())
    symbols = {}
    overflow = 0

    for i, name in enumerate(sorted_names):
        info = defs[name]
        if i < len(pool):
            ch = pool[i]
        else:
            # 理论上不会溢出（池 > Def 数），如果溢出就用私有区兜底
            ch = chr(0xE000 + overflow)
            overflow += 1
        symbols[name] = {
            "char": ch,
            "label": info["label"],
            "group": info["group"],
        }

    # 4. 组装输出
    output = {
        "version": 1,
        "description": (
            "RimWorldMCP 词表文件 — defName 到显示字符的映射。"
            "由 generate_symbols.py 从游戏 XML 自动生成。"
            "生成后应由 AI 对重要 def 手工润色字符（保持一对一、语义匹配）。"
            "修改后删除 RimWorldMCP_SymbolDictionary.json 缓存生效。"
        ),
        "symbols": symbols,
    }

    # 5. 写入
    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    # 统计
    groups = {}
    for info in symbols.values():
        g = info["group"]
        groups[g] = groups.get(g, 0) + 1

    print(f"输出: {OUTPUT_PATH}")
    print(f"条目: {len(symbols)}（溢出 {overflow}）")
    print(f"分类: {dict(groups)}")
    print()
    print("⚠ 下一步: 由 AI 对重要 def 手工润色字符映射，使语义匹配。")
    print("  修改后删除 RimWorld 持久化目录下的 RimWorldMCP_SymbolDictionary.json。")


if __name__ == "__main__":
    main()
