using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Verse;
using Verse.AI;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public class Tool_TendNow : ITool
    {
        public string Name => "tend_now";
        public string Description => "指定殖民者现场治疗目标。支持自动选最佳药品、指定药品或不用药。用于止血、防止暴毙。";

        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                doctor_id = new { type = "integer", description = "执行治疗的殖民者 ID（来自 get_colonists）" },
                patient_id = new { type = "integer", description = "被治疗的目标 ID（来自 get_tile_detail 或 get_colonists）" },
                use_medicine = new { type = "boolean", description = "是否使用药品（默认 true）。false 则不用药治疗", @default = true },
                medicine_defName = new { type = "string", description = "指定药品 DefName（如 MedicineHerbal, MedicineIndustrial, MedicineUltratech）。不指定则自动选最佳可用药品" }
            },
            required = new[] { "doctor_id", "patient_id" }
        });

        public Task<ToolResult> ExecuteAsync(JsonElement? args)
        {
            if (args == null)
                return Task.FromResult(ToolResult.Error("缺少参数"));

            if (!args.Value.TryGetProperty("doctor_id", out var jDid) || !jDid.TryGetInt32(out var doctorId))
                return Task.FromResult(ToolResult.Error("缺少必填参数: doctor_id"));
            if (!args.Value.TryGetProperty("patient_id", out var jPid) || !jPid.TryGetInt32(out var patientId))
                return Task.FromResult(ToolResult.Error("缺少必填参数: patient_id"));

            bool useMedicine = true;
            if (args.Value.TryGetProperty("use_medicine", out var jUm))
                useMedicine = jUm.ValueKind != JsonValueKind.False;

            string? medicineDefName = null;
            if (args.Value.TryGetProperty("medicine_defName", out var jMd) && jMd.GetString() is string md)
                medicineDefName = md;

            return McpCommandQueue.DispatchAsync(() =>
            {
                try
                {
                    var colonists = PawnsFinder.AllMaps_FreeColonistsSpawned;
                    if (colonists == null || colonists.Count == 0)
                        return ToolResult.Error("当前没有自由殖民者。");

                    Pawn doctor = colonists.FirstOrDefault(c => c.thingIDNumber == doctorId);
                    if (doctor == null)
                        return ToolResult.Error($"找不到医生殖民者 ID={doctorId}");

                    Map map = Find.CurrentMap;
                    if (map == null)
                        return ToolResult.Error("没有当前地图。");

                    Pawn patient = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.thingIDNumber == patientId);
                    if (patient == null)
                        return ToolResult.Error($"找不到目标 ID={patientId}");

                    if (doctor == patient)
                        return ToolResult.Error("医生不能给自己治疗。如有需要请设置殖民者的 selfTend 属性。");

                    // 验证病人需要治疗
                    if (!patient.health.HasHediffsNeedingTendByPlayer(false))
                        return ToolResult.Error($"{patient.Name.ToStringShort} 当前没有需要治疗的伤势。");

                    if (!HealthAIUtility.ShouldBeTendedNowByPlayer(patient))
                        return ToolResult.Error($"{patient.Name.ToStringShort} 当前不需要治疗（可能已治疗过或医疗设置为无）。");

                    // 验证可达
                    if (!doctor.CanReach(patient, PathEndMode.OnCell, Danger.Deadly))
                        return ToolResult.Error($"医生 {doctor.Name.ToStringShort} 无法到达 {patient.Name.ToStringShort}。");

                    // 验证可预约
                    if (!doctor.CanReserveAndReach(patient, PathEndMode.OnCell, Danger.Deadly, 1, -1, null, false))
                        return ToolResult.Error($"医生 {doctor.Name.ToStringShort} 无法预约 {patient.Name.ToStringShort}。");

                    // 确定药品
                    Thing? medicine = null;
                    string medLabel = "无";

                    if (useMedicine)
                    {
                        if (!string.IsNullOrEmpty(medicineDefName))
                        {
                            var medDef = DefDatabase<ThingDef>.GetNamed(medicineDefName, false);
                            if (medDef == null)
                                return ToolResult.Error($"找不到药品定义: {medicineDefName}");
                            if (!medDef.IsMedicine)
                                return ToolResult.Error($"{medicineDefName} 不是药品。");

                            // 验证病人的医疗策略允许此药
                            if (patient.playerSettings != null &&
                                !patient.playerSettings.medCare.AllowsMedicine(medDef))
                                return ToolResult.Error(
                                    $"{patient.Name.ToStringShort} 的医疗策略 ({patient.playerSettings.medCare.GetLabel()}) 不允许使用 {medDef.label}。");

                            // 在地图上找此药品
                            medicine = GenClosest.ClosestThing_Global_Reachable(
                                patient.Position, map, map.listerThings.ThingsOfDef(medDef),
                                PathEndMode.ClosestTouch, TraverseParms.For(doctor), 9999f,
                                t => !t.IsForbidden(doctor) && doctor.CanReserve(t, 1, -1, null, false));

                            if (medicine == null)
                                return ToolResult.Error($"地图上找不到可用的 {medDef.label}。");
                            medLabel = medDef.label;
                        }
                        else
                        {
                            // 自动选最佳药品
                            medicine = HealthAIUtility.FindBestMedicine(doctor, patient, false);
                            if (medicine != null)
                                medLabel = medicine.def.label;
                            else
                                useMedicine = false; // 降级为无药治疗
                        }
                    }

                    // 创建治疗 Job
                    Job job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
                    if (!doctor.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                        return ToolResult.Error($"{doctor.Name.ToStringShort} 无法执行治疗（目标可能已被占用或当前任务无法中断）。");

                    // 构建返回信息
                    var sb = new StringBuilder();
                    sb.AppendLine($"医生 {doctor.Name.ToStringShort} 已前往治疗 {patient.Name.ToStringShort}。");
                    sb.AppendLine($"- 使用药品: {(useMedicine ? medLabel : "否（无药治疗，治疗质量较低）")}");

                    // 流血信息
                    float bleedRate = patient.health.hediffSet.BleedRateTotal;
                    if (bleedRate > 0.0001f)
                    {
                        float bleedPctPerDay = bleedRate * 60000f * 100f;
                        int ticksUntilDeath = HealthUtility.TicksUntilDeathDueToBloodLoss(patient);
                        if (ticksUntilDeath < int.MaxValue)
                        {
                            float hours = ticksUntilDeath / 2500f;
                            sb.AppendLine($"- 流血率: {bleedPctPerDay:F1}%/天");
                            sb.AppendLine($"- 预计死亡: {hours:F1} 小时内（{ticksUntilDeath} ticks）");
                        }
                    }

                    // 列出主要伤势
                    var tendableHediffs = patient.health.hediffSet.hediffs
                        .Where(h => h.TendableNow(false))
                        .OrderByDescending(h => h.TendPriority)
                        .Take(5)
                        .ToList();

                    if (tendableHediffs.Count > 0)
                    {
                        sb.AppendLine("- 待治疗伤势:");
                        foreach (var h in tendableHediffs)
                        {
                            string bleeding = h.BleedRate > 0f ? $" (流血: {h.BleedRate * 60000f:F1}%/天)" : "";
                            sb.AppendLine($"  - {h.LabelCap}: 严重度 {h.Severity:F1}{bleeding}");
                        }
                    }

                    if (medicine == null && useMedicine)
                        sb.AppendLine("- 提示: 自动选药未找到可用药品，已降级为无药治疗。");

                    return ToolResult.Success(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return ToolResult.Error($"治疗失败: {ex.Message}");
                }
            });
        }

        public (int x, int y)? GetTargetPos(JsonElement? args)
        {
            return null; // 返回 null，由 doctor 的位置决定视角
        }
    }
}
