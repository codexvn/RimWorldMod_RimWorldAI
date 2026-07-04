"""
generate_symbols.py

从 RimWorld 游戏 XML Def 中提取所有 ThingDef/TerrainDef，为每个 def 分配独立字符，
生成 Symbols.json 词表文件。

用法:
  python3 generate_symbols.py \
    --rimworld-dir F:/SteamLibrary/steamapps/common/RimWorld \
    --output RimWorldMCP/resource/Symbols.json \
    --pool-size 4000

工作流程:
  1. 扫描所有 DLC 的 Defs/ 目录，收集 <ThingDef> 和 <TerrainDef>
  2. 跳过抽象定义（isAbstract=true）
  3. PRESET_MAP 中的 def 使用手工指定语义字符（自检无冲突）
  4. 其余 def 按字母序从 Unicode 池分配
  5. 剩余字符 → fallback_pool
  6. 所有字符排除私有区(U+E000-U+F8FF)、控制字符、代理区

字符映射: 编辑 PRESET_MAP 字典修改语义字符，添加/删除条目。
"""

import argparse
import json
import os
import re
import sys
import unicodedata

# 固定网格独占字符 — 不能分配给词表
RESERVED = set("▓▒░·○◎●█P.?\"'[]{}0123456789,")

# 不可渲染的 Unicode 区块 (私有区/代理/特殊)
BAD_RANGES = [
    (0xE000, 0xF8FF),   # 私有区 — 大多字体无字形
    (0xF0000, 0x10FFFF),# 补充私有区
    (0xD800, 0xDFFF),   # 代理区
    (0xFFF0, 0xFFFF),   # 特殊
]

def is_bad_char(c: str) -> bool:
    """检查字符是否不可渲染"""
    cp = ord(c)
    for lo, hi in BAD_RANGES:
        if lo <= cp <= hi:
            return True
    cat = unicodedata.category(c)
    if cat.startswith("C") and cat != "Co":
        return True
    return False

# ============================================================
# PRESET_MAP — 手工语义字符映射 (defName → char)
# 生成时自检: 无_RESERVED、内部无重复。
# 要修改直接编辑此字典、加大维护即可。不要改后面的自动分配逻辑。
# ============================================================

PRESET_MAP = {
    # ---- 地形 Terrain ----
    "SoilRich":         ":",
    "Gravel":           "`",
    "Sand":             ";",
    "SoftSand":         "∺",
    "PackedDirt":       "∎",
    "Mud":              "%",
    "Marsh":            "≈",
    "Ice":              "=",
    "WaterShallow":     "~",
    "WaterDeep":        "≋",
    "Concrete":         "&",

    # ---- 建筑：墙门 ----
    "Wall":             "▉",
    "Door":             "▣",
    "Column":           "◈",
    "Fence":            "║",
    "FenceGate":        "╬",

    # ---- 建筑：防御 ----
    "Sandbags":         "▦",
    "Barricade":        "▤",
    "TrapSpike":        "▲",
    "Turret_MiniTurret":"⊗",

    # ---- 建筑：电力 ----
    "PowerConduit":     "─",
    "Battery":          "⚡",
    "SolarGenerator":   "☀",
    "WindTurbine":      "☴",
    "WoodFiredGenerator":"♮",
    "GeothermalGenerator":"♁",
    "PowerSwitch":      "⏻",

    # ---- 建筑：温控 ----
    "Heater":           "♨",
    "Cooler":           "❄",
    "Campfire":         "♩",
    "Vent":             "↔",
    "StandingLamp":     "☉",
    "SunLamp":          "☼",
    "WallLamp":         "◐",

    # ---- 建筑：家具 ----
    "Bed":              "□",
    "DoubleBed":        "▥",
    "RoyalBed":         "♛",
    "HospitalBed":      "⚕",
    "Bedroll":          "▭",
    "Crib":             "⌂",
    "DiningChair":      "⌐",
    "Armchair":         "⌒",
    "Stool":            "∙",
    "Table2x2c":        "⊞",
    "Table1x2c":        "⊟",
    "TableButcher":     "⚒",
    "Shelf":            "▨",
    "Dresser":          "▩",

    # ---- 建筑：生产 ----
    "SimpleResearchBench":"⌘",
    "HiTechResearchBench":"⌬",
    "ElectricStove":    "♫",
    "FueledStove":      "♺",
    "NutrientPasteDispenser":"◫",
    "ElectricSmithy":   "⚙",
    "FabricationBench": "⚗",
    "TableStonecutter": "⬢",
    "HydroponicsBasin": "▰",
    "Hopper":           "⌵",

    # ---- 建筑：医疗/杂项 ----
    "VitalsMonitor":    "✚",
    "CommsConsole":     "@",
    "Grave":            "†",
    "CryptosleepCasket":"✖",

    # ---- 建筑：娱乐/艺术 ----
    "HorseshoesPin":    "⊂",
    "ChessTable":       "♜",
    "BilliardsTable":   "◗",
    "PokerTable":       "♠",
    "TubeTelevision":   "◑",
    "SculptureSmall":   "△",
    "SculptureGrand":   "◇",

    # ---- 物品 Item ----
    "Steel":            "■",
    "Plasteel":         "▢",
    "Silver":           "$",
    "Gold":             "◆",
    "Uranium":          "☢",
    "WoodLog":          "♣",
    "ComponentIndustrial":"◍",
    "ComponentSpacer":  "◉",
    "Chemfuel":         "⛽",
    "Neutroamine":      "⚘",
    "MedicineHerbal":   "☘",
    "MedicineIndustrial":"✤",
    "Kibble":           "◌",
    "MealSimple":       "◒",
    "RawRice":          "◓",
    "RawPotatoes":      "◔",
    "RawCorn":          "◕",
    "RawBerries":       "◖",
    "Hay":              "≀",
    "Synthread":        "⌇",
    "Cloth":            "✕",

    # ---- 植物 Plant ----
    "Plant_Rice":       "ρ",
    "Plant_Potato":     "π",
    "Plant_Corn":       "ς",
    "Plant_Cotton":     "τ",
    "Plant_Healroot":   "✿",
    "Plant_Hops":       "♧",
    "Plant_Smokeleaf":  "☁",
    "Plant_Psychoid":   "Ψ",
    "Plant_Devilstrand":"δ",

    "Plant_TreeOak":    "♢",
    "Plant_TreePine":   "♤",
    "Plant_TreePoplar": "┄",
    "Plant_TreeBirch":  "♡",
    "Plant_TreePalm":   "♥",
    "Plant_TreeCocoa":  "☕",
    "Plant_TreeTeak":   "♭",
    "Plant_TreeWillow": "℧",
    "Plant_TreeCypress":"¥",
    "Plant_TreeMaple":  "♬",
    "Plant_TreeCecropia":"✳",
    "Plant_TreeDrago":  "◊",
    "Plant_TreeAnima":  "☯",
    "Plant_TreeGauranlen":"✧",
    "Plant_TreeBamboo": "┃",

    "Plant_Grass":      "˙",
    "Plant_Bush":       "⌈",
    "Plant_Berry":      "β",
    "Plant_Moss":       "≡",
    "Plant_Rose":       "❀",
    "Plant_Daylily":    "❁",

    # ---- Pawn ----
    "Human":            "☺",
    "Muffalo":          "♞",
    "Boomalope":        "♘",
    "Thrumbo":          "♔",
    "Megasloth":        "☻",

    "Mech_CentipedeBlaster":"☣",
    "Mech_Scyther":     "✂",
    "Mech_Lancer":      "⚐",
    "Mech_Pikeman":     "⚑",
}

# ============================================================
# 自检：脚本启动时验证 PRESET_MAP 无冲突
# ============================================================
def check_preset():
    seen = {}
    for name, ch in PRESET_MAP.items():
        if ch in RESERVED:
            raise SystemExit(f"PRESET_MAP 错误: {name}={ch!r} 与固定网格冲突")
        if ch in seen:
            raise SystemExit(f"PRESET_MAP 重复: {name}={ch!r} vs {seen[ch]}={ch!r}")
        if is_bad_char(ch):
            raise SystemExit(f"PRESET_MAP 错误: {name}={ch!r} 是不可渲染字符")
        seen[ch] = name

# ============================================================
# Def 分类
# ============================================================
def classify(def_name: str, tag: str, parent_dir: str) -> str:
    if tag == "TerrainDef":
        return "Terrain"
    p = parent_dir.lower()
    if any(kw in p for kw in [
        "building","furniture","production","power","security",
        "structure","temperature","art","joy","mech","exotic",
        "special","musical","natural","ideo","ritual","deathrest",
        "recharger","fleshmass","obelisk","psychic","void","ship",
        "cult","condition","ancient",
    ]):
        return "Building"
    if "plant" in p:
        return "Plant"
    if any(kw in p for kw in ["race","animal","human","insect","mechanoid","entity","fleshbeast"]):
        return "Pawn"
    return "Item"

# ============================================================
# Unicode 符号池 — 排除不可渲染字符
# ============================================================
def build_pool() -> list[str]:
    """按语义分类的 Unicode 区块，各分类独立收集后按优先级合并"""
    groups = [
        # (分类标签, 区块列表)
        ("结构",  [(0x2500,0x257F)]),                    # 建筑结构
        ("防御",  [(0x2580,0x259F)]),                    # 防御与大型建筑
        ("家具",  [(0x25A0,0x25FF)]),                    # 家具与资源
        ("科技",  [(0x2200,0x22FF)]),                    # 科技与机械
        ("电力",  [(0x2600,0x26FF)]),                    # 电力与特殊设施
        ("艺术",  [(0x2700,0x27BF)]),                    # 艺术与医疗
        ("作物",  [(0x03B1,0x03C9)]),                    # 作物植物(希腊字母)
        ("流向",  [(0x2190,0x21FF)]),                    # 流向类设施
        # 补充区块
        ("补充1", [(0x2300,0x23FF),(0x2B00,0x2BFF)]),   # 杂项技术+箭头
        ("补充2", [(0x00C0,0x024F),(0x0370,0x03FF)]),   # 拉丁扩展+希腊
        ("补充3", [(0x0400,0x04FF),(0x0530,0x06FF)]),   # 西里尔+其他字母
        ("补充4", [(0x3190,0x33FF)]),                    # CJK兼容
        ("补充5", [(0x2800,0x28FF),(0xFF00,0xFFEF)]),   # 盲文+半角
    ]
    pool = []
    for _label, ranges in groups:
        for lo, hi in ranges:
            for cp in range(lo, hi+1):
                c = chr(cp)
                if is_bad_char(c) or c in RESERVED:
                    continue
                pool.append(c)
    seen = set()
    return [c for c in pool if not (c in seen or seen.add(c))]

# ============================================================
# XML 解析
# ============================================================
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
                    content, re.DOTALL)
                for tag_open, block, tag_close in blocks:
                    if tag_open != tag_close:
                        continue
                    if re.search(r"<isAbstract>\s*true\s*</isAbstract>", block, re.IGNORECASE):
                        continue
                    m_name = re.search(r"<defName>(.*?)</defName>", block)
                    if not m_name:
                        continue
                    name = m_name.group(1)
                    parent_dir = os.path.basename(os.path.dirname(path))
                    cat = classify(name, tag_open, parent_dir)
                    if name not in defs:
                        defs[name] = {"group": cat}
    return defs

# ============================================================
# 主流程
# ============================================================
def main():
    check_preset()

    parser = argparse.ArgumentParser(description="从 RimWorld XML 生成 Symbols.json 词表")
    parser.add_argument("--rimworld-dir", required=True,
                        help="RimWorld 安装根目录")
    parser.add_argument("--output", required=True,
                        help="输出 JSON 文件路径")
    parser.add_argument("--dlcs", nargs="+",
                        default=["Core","Ideology","Royalty","Biotech","Anomaly"],
                        help="要扫描的 DLC 列表")
    parser.add_argument("--pool-size", type=int, default=4000,
                        help="兜底池大小")
    args = parser.parse_args()

    print(f"RimWorld: {args.rimworld_dir}")
    print(f"DLCs: {args.dlcs}")

    # 1. 抽取
    defs = extract_defs(args.rimworld_dir, args.dlcs)
    print(f"Def 总数: {len(defs)}")

    # 2. 池
    pool = build_pool()
    print(f"符号池: {len(pool)}")

    # 3. 分配
    symbols = {}
    used = set()   # 已用字符

    # 3a. PRESET_MAP (手工指定)
    from_preset = 0
    for name, ch in PRESET_MAP.items():
        if name not in defs:
            continue
        symbols[name] = {"char": ch, "group": defs[name]["group"]}
        used.add(ch)
        from_preset += 1

    # 3b. ASCII 池剩余 (33-126)
    ascii_count = 0
    for cp in range(33, 127):
        ch = chr(cp)
        if ch in RESERVED or ch in used or is_bad_char(ch):
            continue
        # 找第一个未分配的非preset def
        for name in sorted(defs):
            if name not in symbols:
                symbols[name] = {"char": ch, "group": defs[name]["group"]}
                used.add(ch)
                ascii_count += 1
                break

    # 3c. Unicode 池剩余
    unicode_count = 0
    pi = 0
    remaining = sorted(n for n in defs if n not in symbols)
    for name in remaining:
        while pi < len(pool) and pool[pi] in used:
            pi += 1
        if pi < len(pool):
            ch = pool[pi]; pi += 1
        else:
            break  # 池耗尽（理论上不会）
        symbols[name] = {"char": ch, "group": defs[name]["group"]}
        used.add(ch)
        unicode_count += 1

    # 4. fallback_pool (从未使用字符取)
    fallback_pool = []
    for c in pool[pi:]:
        if c not in used and not is_bad_char(c):
            fallback_pool.append(c)
            if len(fallback_pool) >= args.pool_size:
                break
    # 不足时从周围可见Unicode补充
    extra_cp = 0x2000
    while len(fallback_pool) < args.pool_size:
        c = chr(extra_cp); extra_cp += 1
        if c not in used and not is_bad_char(c) and c not in RESERVED:
            fallback_pool.append(c)

    # 5. 写入
    output = {"version": 1, "symbols": symbols, "fallback_pool": fallback_pool}
    out_dir = os.path.dirname(args.output) or "."
    os.makedirs(out_dir, exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    # 6. 统计
    groups = {}
    for v in symbols.values():
        g = v["group"]; groups[g] = groups.get(g, 0) + 1
    chars = [v["char"] for v in symbols.values()]
    ascii_total = sum(1 for c in chars if ord(c) < 128)
    from collections import Counter
    dupes = [c for c,n in Counter(chars).items() if n>1]
    print(f"输出: {args.output}")
    print(f"条目: {len(symbols)} (预设:{from_preset} ASCII剩余:{ascii_count} Unicode:{unicode_count})")
    print(f"ASCII字符: {ascii_total}  分类: {dict(groups)}")
    print(f"兜底池: {len(fallback_pool)} 一对一: {'OK' if not dupes else 'FAIL:'+str(dupes)}")
    if dupes:
        sys.exit(1)

if __name__ == "__main__":
    main()
