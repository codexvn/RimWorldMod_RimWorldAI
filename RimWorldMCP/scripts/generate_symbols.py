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

生成后由 AI 润色:
  脚本分配的字符是纯机械的 — 按字母序依次从池中取，没有语义关联。
  生成后应由 AI 对重要 def 的字符进行语义优化，例如:

    "Wall":      { "char": "㆛" }  → "char": "#"    墙的语义
    "Door":      { "char": "㆝" }  → "char": "D"    门的语义
    "WaterDeep": { "char": "㇇" }  → "char": "〰"   深水的语义
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

# 固定网格占用的字符
RESERVED_CHARS = set("▓▒░·○◎●█P.?")


# ---- Def 分类 ----

def classify(def_name: str, tag: str, parent_dir: str) -> str:
    if tag == "TerrainDef":
        return "Terrain"

    p = parent_dir.lower()
    if any(kw in p for kw in [
        "building", "furniture", "production", "power", "security",
        "structure", "temperature", "art", "joy", "mech", "exotic",
        "special", "musical", "natural", "ideo", "ritual", "deathrest",
        "recharger", "fleshmass", "obelisk", "psychic", "void", "ship",
        "cult", "condition", "ancient",
    ]):
        return "Building"
    if "plant" in p:
        return "Plant"
    if any(kw in p for kw in ["race", "animal", "human", "insect", "mechanoid", "entity", "fleshbeast"]):
        return "Pawn"
    return "Item"


# ---- Unicode 符号池 ----

def build_pool() -> list[str]:
    ranges = [
        (0x3190, 0x319F), (0x31C0, 0x31EF), (0x3200, 0x32FF), (0x3300, 0x33FF),
        (0x2300, 0x23FF), (0x25A0, 0x25FF), (0x2600, 0x26FF), (0x2700, 0x27BF),
        (0x27C0, 0x27EF), (0x2980, 0x29FF), (0x2B00, 0x2BFF), (0x00C0, 0x024F),
        (0x0370, 0x03FF), (0x0400, 0x04FF), (0x0530, 0x06FF), (0x0E00, 0x0E7F),
        (0x2500, 0x257F), (0x2580, 0x259F), (0x2800, 0x28FF), (0xFF00, 0xFFEF),
    ]
    pool = []
    for lo, hi in ranges:
        for cp in range(lo, hi + 1):
            c = chr(cp)
            cat = unicodedata.category(c)
            if cat.startswith("C") or cat in ("Zs", "Zl", "Zp"):
                continue
            if c in RESERVED_CHARS:
                continue
            pool.append(c)
    seen = set()
    return [c for c in pool if not (c in seen or seen.add(c))]


# ---- XML 解析 ----

def extract_defs(rimworld_dir: str, dlcs: list[str]) -> dict[str, dict]:
    defs = {}
    for dlc in dlcs:
        defs_dir = os.path.join(rimworld_dir, "Data", dlc, "Defs")
        if not os.path.isdir(defs_dir):
            print(f"  [跳过] 目录不存在: {defs_dir}")
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
                blocks = re.findall(
                    r"<(ThingDef|TerrainDef)[^>]*>(.*?)</(ThingDef|TerrainDef)>",
                    content, re.DOTALL,
                )
                for tag_open, block, tag_close in blocks:
                    if tag_open != tag_close:
                        continue
                    if re.search(r"<isAbstract>\s*true\s*</isAbstract>", block, re.IGNORECASE):
                        continue
                    m_name = re.search(r"<defName>(.*?)</defName>", block)
                    if not m_name:
                        continue
                    name = m_name.group(1)
                    m_label = re.search(r"<label>(.*?)</label>", block)
                    label = m_label.group(1) if m_label else name
                    cat = classify(name, tag_open, os.path.basename(os.path.dirname(path)))
                    if name not in defs:
                        defs[name] = {"label": label, "group": cat}
    return defs


# ---- 主流程 ----

def main():
    parser = argparse.ArgumentParser(description="从 RimWorld XML 生成 Symbols.json 词表")
    parser.add_argument("--rimworld-dir", required=True,
                        help="RimWorld 安装目录（如 F:/SteamLibrary/steamapps/common/RimWorld）")
    parser.add_argument("--output", required=True,
                        help="输出 JSON 文件路径")
    parser.add_argument("--dlcs", nargs="+",
                        default=["Core", "Ideology", "Royalty", "Biotech", "Anomaly"],
                        help="要扫描的 DLC 列表，默认: Core Ideology Royalty Biotech Anomaly")
    args = parser.parse_args()

    rimworld_dir = args.rimworld_dir
    output_path = args.output
    dlcs = args.dlcs

    print(f"RimWorld: {rimworld_dir}")
    print(f"DLCs: {dlcs}")

    # 1. 抽取
    defs = extract_defs(rimworld_dir, dlcs)
    print(f"Def 总数: {len(defs)}")

    # 2. 池
    pool = build_pool()
    print(f"符号池: {len(pool)}")

    # 3. 分配
    overflow = 0
    symbols = {}
    for i, name in enumerate(sorted(defs.keys())):
        info = defs[name]
        ch = pool[i] if i < len(pool) else chr(0xE000 + (i - len(pool)))
        if i >= len(pool):
            overflow += 1
        symbols[name] = {"char": ch, "label": info["label"], "group": info["group"]}

    # 4. 写入
    output = {
        "version": 1,
        "symbols": symbols,
    }
    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    # 5. 统计
    groups = {}
    for v in symbols.values():
        groups[v["group"]] = groups.get(v["group"], 0) + 1
    print(f"输出: {output_path}")
    print(f"条目: {len(symbols)}（溢出 {overflow}）")
    print(f"分类: {dict(groups)}")


if __name__ == "__main__":
    main()
