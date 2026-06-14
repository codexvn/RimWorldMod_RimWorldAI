using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Verse;
using RimWorld;
using RimWorldMCP.Constants;

namespace RimWorldMCP
{
    /// <summary>
    /// 战斗日志收集器——用反射提取结构化字段，生成详细 SSE payload。
    /// </summary>
    public static class BattleLogCollector
    {
        public static int LastCollectTick { get; set; }
        public static void Reset() { LastCollectTick = 0; }

        // ===== 反射缓存 =====
        private static readonly FieldInfo[] _riFields = typeof(BattleLogEntry_RangedImpact)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo[] _mcFields = typeof(BattleLogEntry_MeleeCombat)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo[] _drFields = typeof(LogEntry_DamageResult)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static T? GetObjField<T>(object obj, FieldInfo[] fields, string name) where T : class
        {
            var f = fields.FirstOrDefault(x => x.Name == name);
            return f?.GetValue(obj) as T;
        }
        private static T GetValField<T>(object obj, FieldInfo[] fields, string name) where T : struct
        {
            var f = fields.FirstOrDefault(x => x.Name == name);
            return f != null ? (T)f.GetValue(obj) : default;
        }

        // ===== 收集 =====

        public static List<CombatSummary> Collect(int sinceTick, int untilTick)
        {
            var results = new List<CombatSummary>();
            foreach (var battle in Find.BattleLog.Battles)
                foreach (var entry in battle.Entries)
                    if (entry.Tick > sinceTick && entry.Tick <= untilTick)
                        results.Add(MakeSummary(entry));
            return results.OrderBy(s => s.Tick).ToList();
        }

        public static CombatSummary? Extract(LogEntry entry) => MakeSummary(entry);

        private static CombatSummary MakeSummary(LogEntry entry)
        {
            var s = new CombatSummary { Tick = entry.Tick, Text = entry.ToGameStringFromPOV(null, false) };

            if (entry is BattleLogEntry_RangedImpact ri)
            {
                s.Type = "ranged";
                // 攻击者
                var p = GetObjField<Pawn>(ri, _riFields, "initiatorPawn");
                if (p != null) { s.Attacker = p.LabelShort; s.AttackerId = p.thingIDNumber; }
                else s.Attacker = GetObjField<ThingDef>(ri, _riFields, "initiatorThing")?.label ?? "?";
                // 防御者
                p = GetObjField<Pawn>(ri, _riFields, "recipientPawn");
                if (p != null) { s.Defender = p.LabelShort; s.DefenderId = p.thingIDNumber; }
                else s.Defender = GetObjField<ThingDef>(ri, _riFields, "recipientThing")?.label ?? "?";
                // 武器/弹药/掩体
                s.Weapon = GetObjField<ThingDef>(ri, _riFields, "weaponDef")?.label;
                s.Projectile = GetObjField<ThingDef>(ri, _riFields, "projectileDef")?.label;
                s.Cover = GetObjField<ThingDef>(ri, _riFields, "coverDef")?.label;
            }
            else if (entry is BattleLogEntry_MeleeCombat mc)
            {
                s.Type = "melee";
                var p = GetObjField<Pawn>(mc, _mcFields, "initiator");
                if (p != null) { s.Attacker = p.LabelShort; s.AttackerId = p.thingIDNumber; }
                p = GetObjField<Pawn>(mc, _mcFields, "recipientPawn");
                if (p != null) { s.Defender = p.LabelShort; s.DefenderId = p.thingIDNumber; }
                s.Weapon = GetObjField<string>(mc, _mcFields, "toolLabel") ?? "徒手";
            }

            // 格挡/部位/命中：从 LogEntry_DamageResult 读取
            if (entry is LogEntry_DamageResult dr)
            {
                s.Deflected = GetValField<bool>(dr, _drFields, "deflected");
                if (!s.Deflected)
                {
                    var parts = GetObjField<List<BodyPartRecord>>(dr, _drFields, "damagedParts");
                    if (parts != null) s.DamagedParts = parts.Select(p => p.Label).ToList();
                }
                s.Hit = !s.Deflected && s.DamagedParts != null && s.DamagedParts.Count > 0;
            }

            return s;
        }

        // ===== SSE payload =====

        public static object ToPayload(CombatSummary s) => new
        {
            type = "combat",
            attack_type = s.Type,
            attacker = s.Attacker,
            attacker_id = s.AttackerId,
            defender = s.Defender,
            defender_id = s.DefenderId,
            weapon = s.Weapon,
            projectile = s.Projectile,
            cover = s.Cover,
            hit = s.Hit,
            deflected = s.Deflected,
            damaged_parts = s.DamagedParts,
            raw_damage = s.RawDamage,
            actual_damage = s.ActualDamage,
            damage_type = s.DamageType,
            text = s.Text,
            tick = s.Tick
        };

        public static string BuildTextReport(List<CombatSummary> summaries)
        {
            if (summaries.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("### 战斗日志");
            foreach (var s in summaries)
            {
                string dmg = s.ActualDamage > 0 ? $" [{s.ActualDamage:F0}伤害]" : (s.Deflected ? " [被格挡]" : "");
                sb.AppendLine($"- (Tick {s.Tick}){dmg} {s.Text}");
            }
            return sb.ToString().TrimEnd();
        }

        public static void PushAll(List<CombatSummary> summaries)
        {
            foreach (var s in summaries)
            {
                var json = JsonSerializer.Serialize(ToPayload(s));
                McpServiceManager.Host?.SendEvent(McpChannels.GameCombat, json);
            }
        }
    }

    public class CombatSummary
    {
        public int Tick;
        public string Text = "";
        public string Type = "";
        public string Attacker = "";
        public int AttackerId;
        public string Defender = "";
        public int DefenderId;
        public string? Weapon;
        public string? Projectile;
        public string? Cover;
        public bool Hit; // 是否命中（!Deflected && DamagedParts非空）
        public bool Deflected;
        public List<string>? DamagedParts;
        /// <summary>原始伤害（装甲减免前），来自 PostApplyDamage 桥接</summary>
        public float RawDamage;
        /// <summary>实际伤害（装甲减免后），来自 PostApplyDamage 桥接</summary>
        public float ActualDamage;
        /// <summary>伤害类型标签（割伤/瘀伤/子弹等）</summary>
        public string? DamageType;
    }
}
