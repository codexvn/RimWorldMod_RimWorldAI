using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimWorldMCP.AgentRuntime
{
    /// <summary>
    /// Colony Load Score 0~100，动态控制 AI 轮询频率。
    /// 在主线程 GameComponentUpdate 中调用 Tick()。
    /// </summary>
    public static class Scheduler
    {
        /// <summary>当前负载分数 0~100</summary>
        public static int LoadScore { get; private set; }

        /// <summary>当前调度模式</summary>
        public static string Mode { get; private set; } = "Normal";

        /// <summary>上次 Agent 被唤醒的游戏 tick</summary>
        private static int _lastWakeTick;
        private static readonly Dictionary<string, int> _agentLastWake = new();

        /// <summary>每帧调用，更新负载评估。</summary>
        public static void Tick()
        {
            var map = Find.CurrentMap;
            if (map == null) return;

            float threat = CalculateThreat(map);
            float workload = CalculateWorkload(map);
            float resourceStress = CalculateResourceStress(map);

            LoadScore = (int)System.Math.Max(0, System.Math.Min(100, threat * 0.5f + workload * 0.3f + resourceStress * 0.2f));
            Mode = LoadScore switch
            {
                < 20 => "Peace",
                < 40 => "Normal",
                < 60 => "Busy",
                < 80 => "HighPressure",
                _ => "Crisis"
            };
        }

        /// <summary>指定 Agent 是否到了定时唤醒的时间</summary>
        public static bool ShouldWake(string agentName, int intervalGameHours)
        {
            var map = Find.CurrentMap;
            if (map == null) return false;

            int now = Find.TickManager.TicksGame;
            int interval = intervalGameHours * 2500; // 1h = 2500 ticks

            if (!_agentLastWake.TryGetValue(agentName, out var lastWake))
                _agentLastWake[agentName] = now;

            if (now - lastWake >= interval)
            {
                _agentLastWake[agentName] = now;
                _lastWakeTick = now;
                return true;
            }
            return false;
        }

        /// <summary>Crisis 模式（Load >= 80）</summary>
        public static bool IsCrisis => LoadScore >= 80;

        /// <summary>Crisis 模式下的轮询间隔（tick）</summary>
        public static int CrisisIntervalTicks => LoadScore >= 90 ? 300 : 600;

        // ---- 内部计算 ----

        private static float CalculateThreat(Map map)
        {
            float score = 0f;

            // 地图上的敌对单位
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Faction == null || !pawn.Faction.HostileTo(Faction.OfPlayer)) continue;
                if (pawn.Downed) { score += 5f; continue; }
                score += pawn.RaceProps.IsMechanoid ? 15f : 10f;
            }

            // 活跃的 Raid Letter
            foreach (var letter in Find.LetterStack.LettersListForReading)
            {
                var def = letter.def;
                if (def == LetterDefOf.ThreatBig || def == LetterDefOf.ThreatSmall)
                    score += 20f;
            }

            return System.Math.Min(score, 100f);
        }

        private static float CalculateWorkload(Map map)
        {
            int idle = 0, blocked = 0, total = 0;

            foreach (var pawn in PawnsFinder.AllMaps_FreeColonistsSpawned)
            {
                total++;
                if (pawn.mindState?.IsIdle ?? false) idle++;
                var curJob = pawn.CurJob;
                if (curJob != null && curJob.def == JobDefOf.Wait_MaintainPosture) idle++;
            }

            if (total == 0) return 0f;
            return (idle * 30f) / total + blocked * 10f;
        }

        private static float CalculateResourceStress(Map map)
        {
            float score = 0f;

            // 食物评估
            float foodDays = 0f;
            foreach (var th in map.resourceCounter.AllCountedAmounts)
            {
                if (th.Key.IsNutritionGivingIngestible && th.Key.ingestible?.HumanEdible == true)
                    foodDays += th.Value * th.Key.ingestible.CachedNutrition / (5f * 1.6f);
            }
            if (foodDays < 3f) score += 40f;
            else if (foodDays < 5f) score += 20f;

            // 药品评估
            int medCount = map.resourceCounter.GetCount(ThingDefOf.MedicineUltratech)
                + map.resourceCounter.GetCount(ThingDefOf.MedicineIndustrial)
                + map.resourceCounter.GetCount(ThingDefOf.MedicineHerbal);
            if (medCount < 5) score += 20f;
            else if (medCount < 10) score += 5f;

            return System.Math.Min(score, 100f);
        }

        /// <summary>重置 Agent 的定时器（用于 Combat 等手动唤醒场景）</summary>
        public static void MarkWoken(string agentName)
        {
            _agentLastWake[agentName] = Find.TickManager?.TicksGame ?? 0;
        }
    }
}
