using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldMCP.MapRendering
{
    public static class SymbolDictionary
    {
        private static Dictionary<string, char> _forward = new();
        private static Dictionary<char, (string DefName, string Label, string Category)> _reverse = new();
        private static string _dictHash = "";

        // ---- 固定映射规则 ----

        private struct FixedRule
        {
            public string Category;      // 分类: Terrain / Building / Item / Plant
            public char Symbol;
            public Func<Def, bool> Match;
            public string LabelCn;       // 中文标签

            public FixedRule(string cat, char sym, Func<Def, bool> match, string cn)
            {
                Category = cat; Symbol = sym; Match = match; LabelCn = cn;
            }
        }

        private static List<FixedRule> BuildFixedRules()
        {
            var rules = new List<FixedRule>();

            // === 地形 (TerrainDef) ===
            rules.Add(new("地形", '.', d => d.defName == "Soil" || d.defName == "PackedDirt", "土地"));
            rules.Add(new("地形", ':', d => d.defName == "SoilRich", "沃土"));
            rules.Add(new("地形", ',', d => d.defName == "Gravel", "砾石"));
            rules.Add(new("地形", '·', d => d.defName == "Sand" || d.defName == "SoftSand", "沙地"));
            rules.Add(new("地形", '~', d => d.defName == "WaterShallow" || d.defName == "WaterMovingShallow"
                || d.defName == "Marsh" || (d.defName.Contains("Water") && !d.defName.Contains("Deep")), "浅水"));
            rules.Add(new("地形", '〰', d => d.defName == "WaterDeep" || d.defName == "WaterOceanDeep"
                || d.defName == "WaterMovingChestDeep", "深水"));
            rules.Add(new("地形", '≈', d => d.defName == "Mud", "泥地"));
            rules.Add(new("地形", '█', d => d.defName == "Ice" || d.defName.Contains("Ice"), "冰"));
            rules.Add(new("地形", '□', d => d.defName == "Concrete" || d.defName == "AncientTile"
                || d.defName.Contains("Tile") || d.defName.Contains("Flagstone")
                || d.defName.Contains("Paved"), "石砖地"));
            rules.Add(new("地形", '▤', d => d.defName == "WoodPlankFloor"
                || d.defName.Contains("Wood") || d.defName.Contains("Board"), "木地板"));
            rules.Add(new("地形", '▣', d => d.defName == "Carpet" || d.defName.Contains("Carpet"), "地毯"));
            rules.Add(new("地形", '┄', d => d.defName == "Bridge", "木桥"));
            rules.Add(new("地形", '═', d => d.defName == "HeavyBridge", "重桥"));
            rules.Add(new("地形", '◙', d => d.defName.Contains("Lava"), "岩浆"));
            rules.Add(new("地形", '▓', d => d.defName == "BrokenAsphalt" || d.defName == "AncientConcrete", "旧路面"));
            // 地形兜底
            rules.Add(new("地形", '.', d => d is TerrainDef, "土地"));

            // === 建筑 (ThingDef, category=Building) ===
            var td_SteamGeyser = DefDatabase<ThingDef>.GetNamedSilentFail("SteamGeyser");

            rules.Add(new("建筑", '#', d => IsWallDef(d), "墙"));
            rules.Add(new("建筑", 'D', d => IsDoorDef(d), "门"));
            rules.Add(new("建筑", '◻', d => IsBedDef(d), "床"));
            rules.Add(new("建筑", '◺', d => d.defName == "DiningChair" || d.defName == "Stool", "座椅"));
            rules.Add(new("建筑", '▤', d => d.defName == "Table1x2c" || d.defName == "Table2x2c"
                || d.defName == "Table2x4c" || d.defName == "TableButcher", "桌子"));
            rules.Add(new("建筑", '⊞', d => IsWorkTableDef(d), "工作台"));
            rules.Add(new("建筑", '☈', d => IsTurretDef(d), "炮塔"));
            rules.Add(new("建筑", '▼', d => d.defName.Contains("Trap") || d.defName.Contains("TrapIED"), "陷阱"));
            rules.Add(new("建筑", '▦', d => d.defName == "Sandbags" || d.defName == "Barricade"
                || d.defName == "AncientRazorWire", "掩体"));
            rules.Add(new("建筑", '☀', d => d.defName == "SolarGenerator", "太阳能"));
            rules.Add(new("建筑", '◈', d => d.defName == "WindTurbine", "风电"));
            rules.Add(new("建筑", '⚡', d => IsPowerGenDef(d), "电力设备"));
            rules.Add(new("建筑", '┄', d => d.defName == "PowerConduit" || d.defName == "HiddenConduit", "电缆"));
            rules.Add(new("建筑", '%', d => d.defName == "NutrientPasteDispenser" || d.defName == "Hopper"
                || d.defName == "FermentingBarrel", "食物设备"));
            rules.Add(new("建筑", '+', d => d.defName == "VitalsMonitor" || d.defName.Contains("Hospital"), "医疗设备"));
            rules.Add(new("建筑", ';', d => d.defName == "HydroponicsBasin" || d.defName == "PlantPot", "种植设备"));
            rules.Add(new("建筑", '★', d => IsArtDef(d), "雕塑"));
            rules.Add(new("建筑", '○', d => IsTempControlDef(d), "温度/光照"));
            rules.Add(new("建筑", '~', d => d.defName == "SteamGeyser" || d.defName == "GeothermalVent", "喷泉"));
            rules.Add(new("建筑", '☠', d => d.defName == "Grave" || d.defName == "Sarcophagus", "坟墓"));
            rules.Add(new("建筑", '◆', d => d.defName == "CommsConsole" || d.defName == "OrbitalTradeBeacon", "通讯设备"));
            rules.Add(new("建筑", '◇', d => d.defName == "Column" || d.defName.Contains("Column"), "柱子"));
            // 建筑兜底
            rules.Add(new("建筑", 'B', d => IsGenericBuildingDef(d), "其他建筑"));

            // === 物品 (ThingDef, category=Item) ===
            rules.Add(new("物品", '●', d => IsRawResourceDef(d), "原材料"));
            rules.Add(new("物品", '◇', d => d.defName == "ComponentIndustrial" || d.defName == "ComponentSpacer"
                || d.defName.Contains("Component"), "零件"));
            rules.Add(new("物品", '↑', d => IsWeaponDef(d), "武器"));
            rules.Add(new("物品", '▢', d => IsApparelDef(d), "衣物"));
            rules.Add(new("物品", '+', d => IsMedicineDef(d), "药品"));
            rules.Add(new("物品", '!', d => IsDrugDef(d), "成瘾品"));
            rules.Add(new("物品", '%', d => IsFoodDef(d), "食物"));
            rules.Add(new("物品", '☠', d => d.defName == "Corpse" || d.defName.Contains("Corpse"), "尸体"));
            rules.Add(new("物品", '◐', d => d.defName.Contains("Chunk"), "石块"));

            // === 植物 (ThingDef, category=Plant) ===
            rules.Add(new("植物", '♣', d => IsTreeDef(d), "树"));
            rules.Add(new("植物", ';', d => d is ThingDef td && td.category == ThingCategory.Plant, "植物"));

            return rules;
        }

        // ---- 建筑判断辅助方法 ----

        private static bool IsWallDef(Def d)
        {
            if (d is not ThingDef td) return false;
            if (td.defName == "WallLamp") return false;
            if (td.defName == "Wall") return true;
            if (td.defName.Contains("Wall") && td.building != null) return true;
            if (td.building?.isWall == true) return true;
            if (td.Fillage == FillCategory.Full) return true;
            return false;
        }

        private static bool IsDoorDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.defName == "Door" || td.defName == "SecurityDoor"
                || td.defName == "AncientBlastDoor" || td.IsDoor
                || td.defName.Contains("Door");
        }

        private static bool IsBedDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.defName == "Bed" || td.defName == "RoyalBed" || td.defName == "Bedroll"
                || td.defName == "SleepingSpot" || td.defName == "AncientBed"
                || td.defName.Contains("Bed") || typeof(Building_Bed).IsAssignableFrom(td.thingClass);
        }

        private static bool IsWorkTableDef(Def d)
        {
            if (d is not ThingDef td) return false;
            if (td.defName == "TableButcher") return false; // 屠夫桌归桌子
            return typeof(Building_WorkTable).IsAssignableFrom(td.thingClass)
                || td.defName == "SimpleResearchBench"
                || td.defName == "HiTechResearchBench";
        }

        private static bool IsTurretDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return typeof(Building_Turret).IsAssignableFrom(td.thingClass)
                || td.defName.Contains("Turret");
        }

        private static bool IsPowerGenDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.defName == "Battery" || td.defName == "WoodFiredGenerator"
                || td.defName == "ChemfuelPoweredGenerator" || td.defName == "GeothermalGenerator"
                || td.defName == "WatermillGenerator" || td.defName == "BioferriteGenerator";
        }

        private static bool IsArtDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return typeof(Building_Art).IsAssignableFrom(td.thingClass)
                || td.defName == "Statue" || td.defName == "Urn"
                || td.defName == "SteleLarge" || td.defName == "SteleGrand";
        }

        private static bool IsTempControlDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return typeof(Building_TempControl).IsAssignableFrom(td.thingClass)
                || td.defName == "Cooler" || td.defName == "Heater"
                || td.defName == "PassiveCooler" || td.defName == "Campfire"
                || td.defName == "TorchLamp" || td.defName == "StandingLamp"
                || td.defName == "WallLamp";
        }

        private static bool IsGenericBuildingDef(Def d)
        {
            if (d is not ThingDef td) return false;
            if (td.category != ThingCategory.Building) return false;
            return td.altitudeLayer >= AltitudeLayer.Building;
        }

        private static bool IsRawResourceDef(Def d)
        {
            if (d is not ThingDef td) return false;
            if (td.IsStuff) return true;
            return td.defName == "Silver" || td.defName == "Gold" || td.defName == "Jade"
                || td.defName == "Steel" || td.defName == "WoodLog" || td.defName == "Plasteel"
                || td.defName == "Uranium" || td.defName == "Cloth" || td.defName == "Chemfuel"
                || td.defName.Contains("Leather") || td.defName.Contains("Blocks")
                || td.defName.Contains("Wool");
        }

        private static bool IsWeaponDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.IsWeapon || td.IsRangedWeapon || td.IsMeleeWeapon;
        }

        private static bool IsApparelDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.IsApparel;
        }

        private static bool IsMedicineDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.IsMedicine || td.defName.Contains("Medicine");
        }

        private static bool IsDrugDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.IsDrug || td.defName.Contains("Drug");
        }

        private static bool IsFoodDef(Def d)
        {
            if (d is not ThingDef td) return false;
            if (td.IsNutritionGivingIngestible && !td.IsDrug && !td.IsMedicine) return true;
            return td.defName.StartsWith("Meal") || td.defName == "Pemmican"
                || td.defName == "Kibble" || td.defName == "Hay" || td.defName == "BabyFood"
                || td.defName == "Chocolate" || td.defName == "InsectJelly";
        }

        private static bool IsTreeDef(Def d)
        {
            if (d is not ThingDef td) return false;
            return td.plant?.IsTree == true || td.defName.Contains("Tree");
        }

        // ---- 动态分配符号池 ----

        private static readonly List<string> DynamicPool = new()
        {
            // a-z 中避开常用固定符号对应的字母（'d'=门已用, 但我们用 D 不是 d; b 是建筑兜底）
            "a","c","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z",
            // 希腊字母
            "α","β","γ","δ","ε","ζ","η","θ","ι","κ","λ","μ","ν","ξ","ο","π","ρ","σ","τ","υ","φ","χ","ψ","ω",
            // 方块元素
            "■","□","▪","▫","▴","▾","▸","◂","▬","▭","▮","▯","▲","△",
            // 数学符号
            "∑","∏","∫","∆","∇","∂","√","∞","≈","≡","⊕","⊗","⊙","∅","∌",
            // 特殊符号
            "◎","◉","◐","◑","◒","◓","⁂","⁃","※","‡","†","‣","►","◄","◆","◇",
            // 更多 Unicode (无限扩展)
            "①","②","③","④","⑤","⑥","⑦","⑧","⑨","⑩","⑪","⑫","⑬","⑭","⑮","⑯",
        };

        // ---- 公共属性 ----

        public static string DictHash => _dictHash;
        public static int EntryCount => _forward.Count;

        // ---- 初始化 ----

        public static void Initialize()
        {
            try
            {
                // 1. 尝试从 JSON 加载（若 mod 集未变）
                if (Load())
                {
                    McpLog.Info($"[SymbolDictionary] 从文件加载, 条目: {_forward.Count}, Hash: {_dictHash}");
                    return;
                }

                // 2. 重建
                Rebuild();
                Save();
            }
            catch (Exception ex)
            {
                McpLog.Error($"[SymbolDictionary] 初始化失败: {ex.Message}");
                Rebuild(); // fallback
            }
        }

        private static void Rebuild()
        {
            _forward = new();
            _reverse = new();

            var rules = BuildFixedRules();
            var assignedDefNames = new HashSet<string>();

            // 收集所有 Def
            var allDefs = new List<Def>();
            allDefs.AddRange(DefDatabase<ThingDef>.AllDefs);
            allDefs.AddRange(DefDatabase<TerrainDef>.AllDefs);

            // 第一轮: 固定映射
            foreach (var def in allDefs)
            {
                if (assignedDefNames.Contains(def.defName)) continue;

                foreach (var rule in rules)
                {
                    try
                    {
                        if (rule.Match(def))
                        {
                            _forward[def.defName] = rule.Symbol;
                            assignedDefNames.Add(def.defName);

                            // 反向映射: 同符号合并中文标签
                            if (!_reverse.TryGetValue(rule.Symbol, out var existing))
                            {
                                _reverse[rule.Symbol] = (def.defName, def.label ?? def.defName, rule.Category);
                            }
                            break;
                        }
                    }
                    catch { /* 跳过匹配异常的规则 */ }
                }
            }

            // 第二轮: 动态分配未匹配的 Def（按 defName 字母序）
            var unassigned = allDefs
                .Where(d => !assignedDefNames.Contains(d.defName))
                .OrderBy(d => d.defName)
                .ToList();

            int poolIdx = 0;
            foreach (var def in unassigned)
            {
                while (poolIdx < DynamicPool.Count && _reverse.ContainsKey(DynamicPool[poolIdx][0]))
                    poolIdx++;

                char sym;
                if (poolIdx < DynamicPool.Count)
                {
                    sym = DynamicPool[poolIdx][0];
                    poolIdx++;
                }
                else
                {
                    // 极端情况: 用 defName 首字母兜底
                    sym = char.IsLetter(def.defName[0]) ? def.defName[0] : '?';
                }

                string cat = def switch
                {
                    TerrainDef => "地形",
                    ThingDef td when td.category == ThingCategory.Building => "建筑",
                    ThingDef td when td.category == ThingCategory.Item => "物品",
                    ThingDef td when td.category == ThingCategory.Plant => "植物",
                    ThingDef td when td.category == ThingCategory.Pawn => "生物",
                    _ => "其他"
                };

                _forward[def.defName] = sym;
                _reverse[sym] = (def.defName, def.label ?? def.defName, cat);
                assignedDefNames.Add(def.defName);
            }

            // 计算 Hash — 排序后拼接长度，mod 增删必然改变
            var sortedNames = allDefs.Select(d => d.defName).OrderBy(n => n).ToList();
            _dictHash = string.Join(",", sortedNames).Length.ToString("x8");
            McpLog.Info($"[SymbolDictionary] 重建完成, 固定: {_forward.Count - unassigned.Count}, 动态: {unassigned.Count}, Hash: {_dictHash}");
        }

        // ---- 查询方法 ----

        public static char GetChar(string defName)
        {
            if (_forward.TryGetValue(defName, out var c))
                return c;
            return '?';
        }

        public static char GetChar(ThingDef def) => GetChar(def.defName);

        public static char GetChar(TerrainDef def) => GetChar(def.defName);

        public static string Lookup(char c)
        {
            if (_reverse.TryGetValue(c, out var info))
                return $"{info.DefName} ({info.Label}, {info.Category})";
            return $"未知符号: {c}";
        }

        public static Dictionary<char, (string DefName, string Label, string Category)> GetAll() => new(_reverse);

        public static Dictionary<char, (string DefName, string Label, string Category)> GetByChars(IEnumerable<char> chars)
        {
            var result = new Dictionary<char, (string, string, string)>();
            foreach (var c in chars)
            {
                if (_reverse.TryGetValue(c, out var info))
                    result[c] = info;
            }
            return result;
        }

        public static string GetLegendString(HashSet<char> usedSymbols)
        {
            var sb = new StringBuilder();
            foreach (var c in usedSymbols)
            {
                if (_reverse.TryGetValue(c, out var info))
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append(c);
                    sb.Append('=');
                    sb.Append(info.Label);
                }
            }
            return sb.ToString();
        }

        // ---- 持久化 ----

        public static void Save()
        {
            try
            {
                var payload = new
                {
                    hash = _dictHash,
                    forward = _forward.Select(kv => new { d = kv.Key, s = kv.Value.ToString() }).ToList(),
                    reverse = _reverse.Select(kv => new
                    {
                        s = kv.Key.ToString(),
                        n = kv.Value.DefName,
                        l = kv.Value.Label,
                        c = kv.Value.Category
                    }).ToList()
                };

                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
                string path = GetFilePath();
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[SymbolDictionary] 保存失败: {ex.Message}");
            }
        }

        public static bool Load()
        {
            try
            {
                string path = GetFilePath();
                if (!File.Exists(path)) return false;

                string json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string savedHash = "";
                if (root.TryGetProperty("hash", out var h))
                    savedHash = h.GetString() ?? "";

                // 验证 mod 集是否一致
                var allDefs = new List<Def>();
                allDefs.AddRange(DefDatabase<ThingDef>.AllDefs);
                allDefs.AddRange(DefDatabase<TerrainDef>.AllDefs);
                var sortedNames = allDefs.Select(d => d.defName).OrderBy(n => n).ToList();
                string currentHash = string.Join(",", sortedNames).Length.ToString("x8");

                if (savedHash != currentHash)
                {
                    McpLog.Info($"[SymbolDictionary] 字典 Hash 不匹配 ({savedHash} vs {currentHash}), 重建");
                    return false;
                }

                _forward = new();
                _reverse = new();

                if (root.TryGetProperty("forward", out var fwd))
                {
                    foreach (var item in fwd.EnumerateArray())
                    {
                        var d = item.GetProperty("d").GetString() ?? "";
                        var s = item.GetProperty("s").GetString() ?? "?";
                        if (d != "" && s != "")
                            _forward[d] = s[0];
                    }
                }

                if (root.TryGetProperty("reverse", out var rev))
                {
                    foreach (var item in rev.EnumerateArray())
                    {
                        var s = item.GetProperty("s").GetString() ?? "?";
                        var n = item.GetProperty("n").GetString() ?? "";
                        var l = item.GetProperty("l").GetString() ?? n;
                        var c = item.GetProperty("c").GetString() ?? "";
                        if (s != "")
                            _reverse[s[0]] = (n, l, c);
                    }
                }

                _dictHash = savedHash;
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[SymbolDictionary] 加载失败: {ex.Message}");
                return false;
            }
        }

        private static string GetFilePath()
        {
            return Path.Combine(Application.persistentDataPath, "RimWorldMCP_SymbolDictionary.json");
        }
    }
}
