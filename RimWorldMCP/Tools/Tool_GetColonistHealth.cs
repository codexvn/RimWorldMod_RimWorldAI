using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using RimWorld;
using RimWorldMCP;

namespace RimWorldMCP.Tools
{
    public class Tool_GetColonistHealth : ITool
    {
        public string Name => "get_colonist_health";
        public string Description => "获取殖民者的详细健康报告。包括伤势、疾病、身体部位状态、手术需求等。";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                colonist_id = new { type = "integer", description = "殖民者 ID（来自 get_colonists），不传返回全部" },
                colonist_name = new { type = "string", description = "殖民者名称（模糊匹配），不传返回全部" }
            }
        });

        public async Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            int? colonistId = null;
            string nameFilter = "";
            if (args != null)
            {
                if (args.Value.TryGetProperty("colonist_id", out var jId) && jId.TryGetInt32(out var cid))
                    colonistId = cid;
                if (args.Value.TryGetProperty("colonist_name", out var n))
                    nameFilter = n.GetString() ?? "";
            }

            return await McpCommandQueue.DispatchAsync(() =>
            {
                var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                if (colonists == null || colonists.Count == 0)
                    return ToolResult.Success("## 殖民者健康报告\n\n暂无自由殖民者。");

                IEnumerable<Pawn> filtered = colonists;
                if (colonistId.HasValue)
                    filtered = colonists.Where(c => c.thingIDNumber == colonistId.Value);
                else if (!string.IsNullOrEmpty(nameFilter))
                    filtered = colonists.Where(c =>
                        c.Name.ToStringShort.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        c.Name.ToStringFull.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

                var items = filtered.OrderBy(p => p.thingIDNumber).ToList();
                if (items.Count == 0)
                    return ToolResult.Success("没有匹配的殖民者。");

                var sb = new StringBuilder();
                sb.AppendLine($"## 殖民者健康报告 ({items.Count} 人)");

                foreach (var pawn in items)
                {
                    string name = pawn.Name.ToStringShort;
                    sb.AppendLine();
                    sb.AppendLine($"### {name}");

                    var hediffs = pawn.health?.hediffSet?.hediffs;
                    if (hediffs == null || hediffs.Count == 0)
                    {
                        sb.AppendLine("- 状态: 健康，无异常");
                        continue;
                    }

                    // 分类 hediffs
                    var injuries = new List<Hediff>();
                    var diseases = new List<Hediff>();
                    var implants = new List<Hediff>();
                    var chronic = new List<Hediff>();
                    var other = new List<Hediff>();

                    foreach (var h in hediffs)
                    {
                        if (!h.Visible) continue;
                        if (h.def.defName == "Anesthetic" || h.def.defName == "Sedated") continue;

                        if (h.def.isBad)
                        {
                            if (h.IsPermanent())
                                chronic.Add(h);
                            else if (h.def.hediffClass != null)
                            {
                                // Check if it's a disease/infection
                                var typeName = h.def.hediffClass.Name;
                                if (typeName.Contains("Injury") || typeName.Contains("Wound") || typeName.Contains("Cut")
                                    || typeName.Contains("Bruise") || typeName.Contains("Burn") || typeName.Contains("Crush")
                                    || typeName.Contains("Bite") || typeName.Contains("Stab") || typeName.Contains("Scratch")
                                    || typeName.Contains("Frostbite"))
                                    injuries.Add(h);
                                else if (typeName.Contains("Disease") || typeName.Contains("Infection")
                                    || typeName.Contains("Flu") || typeName.Contains("Plague") || typeName.Contains("Malaria")
                                    || typeName.Contains("Sickness") || typeName.Contains("Poison"))
                                    diseases.Add(h);
                                else
                                    other.Add(h);
                            }
                            else
                            {
                                other.Add(h);
                            }
                        }
                        else
                        {
                            // Good or neutral: implants, missing parts
                            if (h.def.defName.Contains("Bionic") || h.def.defName.Contains("Archotech")
                                || h.def.defName.Contains("Prosthetic") || h.def.defName.Contains("Implant")
                                || h.def.defName.Contains("Peg") || h.def.defName.Contains("Wooden"))
                                implants.Add(h);
                            else
                                other.Add(h);
                        }
                    }

                    // 伤势
                    if (injuries.Count > 0)
                    {
                        sb.AppendLine("- **伤势:**");
                        foreach (var h in injuries)
                        {
                            sb.AppendLine(FormatHediffLine(h));
                        }
                    }

                    // 疾病
                    if (diseases.Count > 0)
                    {
                        sb.AppendLine("- **疾病:**");
                        foreach (var h in diseases)
                        {
                            sb.AppendLine(FormatHediffLine(h));
                        }
                    }

                    // 慢性/永久
                    if (chronic.Count > 0)
                    {
                        sb.AppendLine("- **永久/慢性:**");
                        foreach (var h in chronic)
                        {
                            sb.AppendLine(FormatHediffLine(h));
                        }
                    }

                    // 植入体
                    if (implants.Count > 0)
                    {
                        sb.AppendLine("- **植入体/假肢:**");
                        foreach (var h in implants)
                        {
                            sb.AppendLine(FormatHediffLine(h));
                        }
                    }

                    // 其他
                    if (other.Count > 0)
                    {
                        sb.AppendLine("- **其他:**");
                        foreach (var h in other)
                        {
                            sb.AppendLine(FormatHediffLine(h));
                        }
                    }

                    if (injuries.Count == 0 && diseases.Count == 0 && chronic.Count == 0 && implants.Count == 0 && other.Count == 0)
                    {
                        sb.AppendLine("- 状态: 健康，无异常");
                    }

                    // 关键警告
                    bool hasSerious = injuries.Any(h => h.Severity > 0.5f) || diseases.Any(h => h.Severity > 0.5f);
                    bool hasLifeThreatening = injuries.Any(h => h.def.everCurableByItem == false) ||
                                              diseases.Any(h => h.def.lethalSeverity > 0f);
                    bool hasBleeding = injuries.Any(h => h.BleedRate > 0.01f);

                    if (hasLifeThreatening)
                        sb.AppendLine("- ⚠ **急迫: 存在危及生命的状况，需要立即手术或治疗！**");
                    else if (hasBleeding)
                        sb.AppendLine("- ⚠ **注意: 有流血伤口，需要尽快包扎！**");
                    else if (hasSerious)
                        sb.AppendLine("- ⚠ **注意: 有严重伤势/疾病需要治疗**");
                }

                return ToolResult.Success(sb.ToString());
            });
        }

        private static string FormatHediffLine(Hediff h)
        {
            string part = h.Part?.Label ?? "";
            string bodyPart = !string.IsNullOrEmpty(part) ? $" ({part})" : "";
            string severity = h.Severity > 0.01f ? $" [严重度: {(int)(h.Severity * 100)}%]" : "";
            string bleedRate = h.BleedRate > 0.01f ? $" [流血: {h.BleedRate * 100:F0}%/天]" : "";
            string permanent = h.IsPermanent() ? " [永久]" : "";
            string immun = "";
            try { if (h.TryGetComp<HediffComp_Immunizable>() != null) immun = " [发展中]"; } catch (Exception) { }

            return $"  - {h.Label}{bodyPart}{severity}{bleedRate}{permanent}{immun}";
        }
        public (int minX, int minZ, int maxX, int maxZ)? GetTargetRange(JsonElement? args) => null;
    }
}
