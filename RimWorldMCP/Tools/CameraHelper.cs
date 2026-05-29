using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using RimWorld;

namespace RimWorldMCP.Tools
{
    public static class CameraHelper
    {
        private const float MaxZoomOut = 45f;
        private const float MinZoomIn = 25f;

        /// <summary>移动到区域中心，自动缩放到舒适视野（12~45 格）</summary>
        public static async Task MoveToRange(int minX, int minZ, int maxX, int maxZ)
        {
            await McpCommandQueue.DispatchAsync<object?>(() =>
            {
                var map = Find.CurrentMap;
                if (map == null) return null;

                var driver = Find.CameraDriver;
                float aspect = (float)UI.screenWidth / (float)UI.screenHeight;

                float centerX = (minX + maxX + 1) / 2f;
                float centerZ = (minZ + maxZ + 1) / 2f;
                float rectW = (maxX - minX + 1);
                float rectH = (maxZ - minZ + 1);

                // 加 30% 留白，计算能舒适容纳目标区域的缩格大小
                float needSizeW = (rectW * 1.3f) / (2f * aspect);
                float needSizeH = (rectH * 1.3f) / 2f;
                float needSize = Mathf.Max(needSizeW, needSizeH);
                float idealSize = Mathf.Clamp(needSize, MinZoomIn, MaxZoomOut);
                float curSize = driver.RootSize;

                // 当前缩放与理想值差异小于 20% 时不调整（避免抖）
                float targetSize;
                if (Mathf.Abs(curSize - idealSize) / Mathf.Max(curSize, 0.01f) < 0.2f)
                    targetSize = curSize;
                else
                    targetSize = idealSize;

                float targetY = 15f + (targetSize - driver.config.sizeRange.min)
                    / (driver.config.sizeRange.max - driver.config.sizeRange.min) * 50f;
                var targetLoc = new Vector3(centerX, targetY, centerZ);

                driver.PanToMapLocAndSize(targetLoc, targetSize, 0.35f);

                return null;
            });

            await Task.Delay(400);
        }

        // ============== 自动追踪殖民者集群 ==============

        private static double _lastAutoTrackCheckReal;
        private static double _noColonistVisibleSince;
        private const double AutoTrackCheckIntervalSec = 5.0;
        private const double AutoTrackTriggerDelaySec = 3.0;
        private const float ClusterDistance = 20f;

        /// <summary>
        /// 每帧由 GameComponentUpdate 末尾调用。如果视角长时间无殖民者，自动聚类并移动到最高优先级簇。
        /// </summary>
        public static void AutoTrackColonistsTick()
        {
            if (RimWorldMCPMod.Instance?.Settings?.AutoTrackColonists != true)
                return;
            if (Find.TickManager?.Paused == true)
                return;

            var now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastAutoTrackCheckReal < AutoTrackCheckIntervalSec)
                return;
            _lastAutoTrackCheckReal = now;

            var map = Find.CurrentMap;
            if (map == null) return;

            var colonists = map.mapPawns.FreeColonistsSpawned;

            if (colonists.Count > 0)
            {
                var viewRect = Find.CameraDriver.CurrentViewRect;

                // 检查当前视野内是否有殖民者
                bool anyVisible = false;
                foreach (var c in colonists)
                {
                    if (viewRect.Contains(c.Position))
                    {
                        anyVisible = true;
                        break;
                    }
                }

                if (anyVisible)
                {
                    _noColonistVisibleSince = now;
                    return;
                }
            }

            // 累计未见到殖民者的时间
            if (_noColonistVisibleSince <= 0)
                _noColonistVisibleSince = now;
            if (now - _noColonistVisibleSince < AutoTrackTriggerDelaySec)
                return;

            if (colonists.Count > 0)
            {
                // 触发追踪：聚类 + 评分 + 移动（fire-and-forget，不阻塞帧）
                var bestCluster = FindBestCluster(colonists);
                if (bestCluster == null) return;

                _ = MoveToRange(bestCluster.Value.minX, bestCluster.Value.minZ,
                               bestCluster.Value.maxX, bestCluster.Value.maxZ);
            }
            else
            {
                // 无殖民者：全景俯瞰地图中心
                _ = MoveToRange(0, 0, map.Size.x - 1, map.Size.z - 1);
            }
        }

        /// <summary>殖民者工作 -> 权重映射</summary>
        /// <summary>职业 defName -> 权重映射</summary>
        private static readonly Dictionary<string, int> JobWeightMap = new()
        {
            { "AttackMelee", 10 }, { "AttackStatic", 10 },
            { "WaitAndAttack", 10 }, { "Manhunter", 10 },
            { "FleeAndPanic", 10 }, { "TendPatient", 10 },
            { "TendSelf", 10 }, { "VisitPatient", 10 },
            { "Arrest", 10 }, { "Capture", 10 }, { "TakeToBed", 10 },
            { "ExtinguishSelf", 10 }, { "FightFire", 10 },
            { "Hunt", 10 },
            { "WaitCombat", 8 },
            { "BuildRoof", 5 }, { "RemoveRoof", 5 },
            { "Construct", 5 }, { "Deconstruct", 5 },
            { "Mine", 5 }, { "Smelt", 5 }, { "Craft", 5 },
            { "Sow", 5 }, { "Harvest", 5 },
            { "CutPlant", 2 }, { "HaulToCell", 2 },
            { "HaulToContainer", 2 }, { "Clean", 2 },
            { "Research", 2 }, { "Wait", 1 },
            { "WaitDowned", 1 }, { "Sleep", 1 },
            { "Goto", 1 }, { "StandAndBeStill", 1 },
        };

        private static int GetColonistWeight(Pawn pawn)
        {
            try
            {
                if (pawn.Drafted) return 10;
                if (pawn.mindState.IsIdle) return 1;
                var jd = pawn.CurJobDef;
                if (jd != null && JobWeightMap.TryGetValue(jd.defName, out var w)) return w;
                return 3;
            }
            catch { return 1; }
        }

        /// <summary>连通分量聚类：距离 ≤ ClusterDistance 的殖民者归为一簇</summary>
        private static (int minX, int minZ, int maxX, int maxZ)? FindBestCluster(List<Pawn> colonists)
        {
            int n = colonists.Count;
            if (n == 0) return null;

            // 提取位置
            var positions = new List<(Pawn pawn, IntVec3 pos)>();
            foreach (var c in colonists)
                if (c.Spawned)
                    positions.Add((c, c.Position));

            if (positions.Count == 0) return null;

            var visited = new bool[positions.Count];
            int bestScore = -1;
            (int minX, int minZ, int maxX, int maxZ)? bestCluster = null;

            for (int i = 0; i < positions.Count; i++)
            {
                if (visited[i]) continue;

                // BFS 找簇
                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                int minX = positions[i].pos.x, minZ = positions[i].pos.z;
                int maxX = positions[i].pos.x, maxZ = positions[i].pos.z;
                int clusterScore = 0;

                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    var p_pos = positions[idx].pos;
                    clusterScore += GetColonistWeight(positions[idx].pawn);
                    minX = Math.Min(minX, p_pos.x);
                    minZ = Math.Min(minZ, p_pos.z);
                    maxX = Math.Max(maxX, p_pos.x);
                    maxZ = Math.Max(maxZ, p_pos.z);

                    for (int j = 0; j < positions.Count; j++)
                    {
                        if (visited[j]) continue;
                        var d = Mathf.Sqrt(
                            (positions[j].pos.x - p_pos.x) * (positions[j].pos.x - p_pos.x) +
                            (positions[j].pos.z - p_pos.z) * (positions[j].pos.z - p_pos.z));
                        if (d <= ClusterDistance)
                        {
                            visited[j] = true;
                            queue.Enqueue(j);
                        }
                    }
                }

                if (clusterScore > bestScore)
                {
                    bestScore = clusterScore;
                    bestCluster = (minX, minZ, maxX, maxZ);
                }
            }

            return bestCluster;
        }

        internal static Pawn? FindPawnById(Map map, int id)
        {
            foreach (var p in map.mapPawns.AllPawnsSpawned)
                if (p.thingIDNumber == id) return p;
            return null;
        }

        internal static Thing? FindThingById(Map map, int id)
        {
            foreach (var t in map.listerThings.AllThings)
                if (t.thingIDNumber == id) return t;
            return null;
        }
    }
}
