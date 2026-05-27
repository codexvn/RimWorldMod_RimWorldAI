using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace RimWorldMCP.Tools
{
    public static class CameraHelper
    {
        private const float MaxZoomOut = 40f;

        /// <summary>移动到区域中心，默认 1x 缩放，仅在目标区域超出可视范围时拉远</summary>
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

                float needSizeW = (rectW * 1.05f) / (2f * aspect);
                float needSizeH = (rectH * 1.05f) / 2f;
                float needSizeRaw = Mathf.Max(needSizeW, needSizeH);
                float minSize = driver.config.sizeRange.min;

                float targetSize =
                    needSizeRaw <= minSize
                        ? minSize  // 1x 放得下 → 默认 1x
                        : Mathf.Min(Mathf.Clamp(needSizeRaw, minSize, driver.config.sizeRange.max), MaxZoomOut);

                float targetY = 15f + (targetSize - driver.config.sizeRange.min)
                    / (driver.config.sizeRange.max - driver.config.sizeRange.min) * 50f;
                var targetLoc = new Vector3(centerX, targetY, centerZ);

                driver.PanToMapLocAndSize(targetLoc, targetSize, 0.35f);

                return null;
            });

            await Task.Delay(400);
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
