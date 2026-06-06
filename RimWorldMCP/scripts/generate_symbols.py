"""
generate_symbols.py

从 RimWorld 游戏 XML Def 中提取所有 ThingDef/TerrainDef，为每个 def 分配独立 Unicode 字符，
生成 Symbols.json 词表文件。

用法:
  python3 generate_symbols.py \
    --rimworld-dir F:/SteamLibrary/steamapps/common/RimWorld \
    --output RimWorldMCP/resource/Symbols.json \
    --dlcs Core Ideology Royalty Biotech Anomaly

工作流程:
  1. 扫描所有 DLC 的 Defs/ 目录，收集 <ThingDef> 和 <TerrainDef>
  2. 跳过抽象定义（isAbstract=true）
  3. 按 defName 字母序从 Unicode 符号池分配独立字符
  4. 维护一对一映射：一个字符 = 一个 def
  5. 每个条目包含 char（映射字符）和 group（分类标签）

生成后由 AI 润色:
  脚本分配的字符是纯机械的 — 按字母序依次从池中取，没有语义关联。
  生成后应由 AI 对重要 def 的字符进行语义优化，例如:

    "Wall":      { "char": "㆛" }  → "char": "#"    墙的语义
    "Door":      { "char": "㆝" }  → "char": "D"    门的语义
    "WaterDeep": { "char": "㇇" } → "char": "〰"   深水的语义
    "Soil":      { "char": "㇍" }  → "char": "."    土地的语义

  原则:
    - 保持一对一（改之前先确认目标字符未被占用）
    - 优先用 ASCII 和常见符号
    - 字符与 def 语义尽量匹配
    - 固定网格占用的字符不要用: ▓ ▒ ░ · ○ ◎ ● █ P .

生效:
  SymbolDictionary 每次启动直接读取 Symbols.json 并重建映射，无缓存。
  修改后重启 RimWorld 即生效。
"""

import argparse
import json
import os
import re
import sys
import unicodedata

# ============================================================
# 全局常量
# ============================================================

# 固定网格（fertility / temperature / pollution）独占的字符，
# 词表不能分配这些字符，否则图例会混乱。
RESERVED_CHARS = set("▓▒░·○◎●█P.?")


# ============================================================
# Def 分类 — 根据 XML 标签和所在目录名推断语义分组
# ============================================================

def classify(def_name: str, tag: str, parent_dir: str) -> str:
    """
    返回 def 的分类标签，用于 Symbols.json 的 group 字段。

    参数:
      def_name: Def 的 defName（如 "Wall"、"Sand"）
      tag: XML 标签名 — "ThingDef" 或 "TerrainDef"
      parent_dir: 该 XML 文件所在子目录名（如 "Buildings_Structure"）

    分类规则:
      - TerrainDef        → "Terrain"
      - 目录含建筑关键词   → "Building"
      - 目录含"plant"     → "Plant"
      - 目录含生物关键词   → "Pawn"
      - 其余 ThingDef     → "Item"
    """
    # 地形直判
    if tag == "TerrainDef":
        return "Terrain"

    p = parent_dir.lower()

    # 建筑: 目录名含以下任一关键词
    if any(kw in p for kw in [
        "building", "furniture", "production", "power", "security",
        "structure", "temperature", "art", "joy", "mech", "exotic",
        "special", "musical", "natural", "ideo", "ritual", "deathrest",
        "recharger", "fleshmass", "obelisk", "psychic", "void", "ship",
        "cult", "condition", "ancient",
    ]):
        return "Building"

    # 植物
    if "plant" in p:
        return "Plant"

    # 生物
    if any(kw in p for kw in [
        "race", "animal", "human", "insect", "mechanoid", "entity", "fleshbeast",
    ]):
        return "Pawn"

    # 其余全部归为物品
    return "Item"


# ============================================================
# Unicode 符号池 — 收集等宽渲染友好的可打印字符
# ============================================================

def build_pool() -> list[str]:
    """
    从 20 个 Unicode 区块收集单字符用作 def 映射。

    选择原则:
      - 避开控制字符、空白、代理区、私有区
      - 避开 RESERVED_CHARS（已被 fertility/temperature/pollution 网格占用）
      - 优先选择等宽渲染友好的区块: CJK 兼容方块、几何图形、技术符号等
      - 按 Unicode 码点升序收集，再按区块优先级重新排列

    返回去重保序的字符列表，长度 ≈3700，远大于 Def 总数（~1560），
    保证每个 def 都能分到独立字符。
    """
    # Unicode 区块列表 — 元组 (起始码点, 结束码点)
    ranges = [
        (0x3190, 0x319F),   # 汉文训读标记（小号方框内字符）
        (0x31C0, 0x31EF),   # CJK 笔画
        (0x3200, 0x32FF),   # 带圈/括号 CJK（256 个等宽字符）
        (0x3300, 0x33FF),   # CJK 兼容方块
        (0x2300, 0x23FF),   # 杂项技术符号
        (0x25A0, 0x25FF),   # 几何图形（方块/三角/圆形）
        (0x2600, 0x26FF),   # 杂项符号（星形/天气/棋子）
        (0x2700, 0x27BF),   # 装饰符号（剪刀/星标）
        (0x27C0, 0x27EF),   # 数学符号 A
        (0x2980, 0x29FF),   # 数学符号 B
        (0x2B00, 0x2BFF),   # 补充箭头/图形
        (0x00C0, 0x024F),   # 拉丁扩展（带变音符字母）
        (0x0370, 0x03FF),   # 希腊/科普特字母
        (0x0400, 0x04FF),   # 西里尔字母
        (0x0530, 0x06FF),   # 亚美尼亚/希伯来/阿拉伯字母
        (0x0E00, 0x0E7F),   # 泰文
        (0x2500, 0x257F),   # 制表符（线条/边框）
        (0x2580, 0x259F),   # 方块元素
        (0x2800, 0x28FF),   # 盲文（256 个均匀点阵图案）
        (0xFF00, 0xFFEF),   # 半角/全角形式
    ]

    pool = []
    for lo, hi in ranges:
        for cp in range(lo, hi + 1):
            c = chr(cp)
            cat = unicodedata.category(c)

            # 跳过不可见字符
            # C*: 控制字符 (Cc/Cf/Cs/Co/Cn)
            # Zs: 空格分隔符, Zl: 行分隔符, Zp: 段分隔符
            if cat.startswith("C") or cat in ("Zs", "Zl", "Zp"):
                continue

            # 跳过固定网格占用的字符
            if c in RESERVED_CHARS:
                continue

            pool.append(c)

    # 去重保序: 不同 Unicode 区块可能有重叠码点
    seen = set()
    unique = []
    for c in pool:
        if c not in seen:
            seen.add(c)
            unique.append(c)

    return unique


# ============================================================
# XML 解析 — 从游戏 Def 文件中抽取 ThingDef 和 TerrainDef
# ============================================================

def extract_defs(rimworld_dir: str, dlcs: list[str]) -> dict[str, dict]:
    """
    遍历每个 DLC 的 Defs/ 目录下所有 XML 文件，提取 <ThingDef> 和 <TerrainDef>。

    处理逻辑:
      - 只匹配成对的 <ThingDef>...</ThingDef> / <TerrainDef>...</TerrainDef>
      - 跳过 isAbstract=true 的模板定义（不会在游戏中实际生成）
      - 提取 defName（必须）和分类
      - 根据父目录名调用 classify() 推断分组

    参数:
      rimworld_dir: RimWorld 安装根目录
      dlcs: DLC 目录名列表

    返回:
      {defName: {"group": str}, ...}
    """
    defs = {}

    for dlc in dlcs:
        defs_dir = os.path.join(rimworld_dir, "Data", dlc, "Defs")
        if not os.path.isdir(defs_dir):
            print(f"  [跳过] 目录不存在: {defs_dir}")
            continue

        # os.walk 递归遍历子目录
        for root, _, files in os.walk(defs_dir):
            for fname in files:
                if not fname.endswith(".xml"):
                    continue

                path = os.path.join(root, fname)
                try:
                    content = open(path, encoding="utf-8").read()
                except Exception:
                    continue  # 文件读取失败则跳过

                # 非贪婪匹配: 提取每对 ThingDef/TerrainDef 之间的内容
                # re.DOTALL: . 匹配换行符
                blocks = re.findall(
                    r"<(ThingDef|TerrainDef)[^>]*>(.*?)</(ThingDef|TerrainDef)>",
                    content, re.DOTALL,
                )

                for tag_open, block, tag_close in blocks:
                    # 防御: 确保开闭标签一致（嵌套 Def 时可能不匹配）
                    if tag_open != tag_close:
                        continue

                    # 抽象模板不生成实例，跳过
                    if re.search(
                        r"<isAbstract>\s*true\s*</isAbstract>",
                        block, re.IGNORECASE,
                    ):
                        continue

                    # defName 是必须字段
                    m_name = re.search(r"<defName>(.*?)</defName>", block)
                    if not m_name:
                        continue
                    name = m_name.group(1)

                    # 根据父目录推断分类
                    parent_dir = os.path.basename(os.path.dirname(path))
                    cat = classify(name, tag_open, parent_dir)

                    # 第一次遇到的 defName 为准（后续同名 def 忽略）
                    if name not in defs:
                        defs[name] = {"group": cat}

    return defs


# ============================================================
# 主流程
# ============================================================

def main():
    # ----- 参数解析 -----
    parser = argparse.ArgumentParser(
        description="从 RimWorld XML 生成 Symbols.json 词表"
    )
    parser.add_argument(
        "--rimworld-dir", required=True,
        help="RimWorld 安装根目录（如 F:/SteamLibrary/steamapps/common/RimWorld）",
    )
    parser.add_argument(
        "--output", required=True,
        help="输出 JSON 文件路径",
    )
    parser.add_argument(
        "--dlcs", nargs="+",
        default=["Core", "Ideology", "Royalty", "Biotech", "Anomaly"],
        help="要扫描的 DLC 目录名列表，默认: Core Ideology Royalty Biotech Anomaly",
    )
    args = parser.parse_args()

    rimworld_dir = args.rimworld_dir
    output_path = args.output
    dlcs = args.dlcs

    print(f"RimWorld: {rimworld_dir}")
    print(f"DLCs: {dlcs}")

    # ----- 1. 抽取所有 Def -----
    defs = extract_defs(rimworld_dir, dlcs)
    print(f"Def 总数: {len(defs)}")

    # ----- 2. 构建符号池 -----
    pool = build_pool()
    print(f"符号池: {len(pool)}")

    # ----- 3. 按 defName 字母序分配字符 -----
    # 字母序保证每次运行分配结果完全一致（只要 def 列表不变）
    overflow = 0          # 溢出计数（理论上不会发生，池 > Def 数）
    symbols = {}

    for i, name in enumerate(sorted(defs.keys())):
        info = defs[name]

        # 从池中取第 i 个字符，池不足时回退到私有区 U+E000+
        if i < len(pool):
            ch = pool[i]
        else:
            ch = chr(0xE000 + (i - len(pool)))
            overflow += 1

        # 组装条目
        symbols[name] = {"char": ch, "group": info["group"]}

    # ----- 4. 序列化写入 -----
    output = {"version": 1, "symbols": symbols}

    out_dir = os.path.dirname(output_path) or "."
    os.makedirs(out_dir, exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    # ----- 5. 统计输出 -----
    groups = {}
    for v in symbols.values():
        g = v["group"]
        groups[g] = groups.get(g, 0) + 1

    print(f"输出: {output_path}")
    print(f"条目: {len(symbols)}（溢出: {overflow}）")
    print(f"分类: {dict(groups)}")
    print()
    print("下一步: 由 AI 对重要 def 手工润色字符映射，使语义匹配。")
    print("  修改后重启 RimWorld 即生效。")


if __name__ == "__main__":
    main()
