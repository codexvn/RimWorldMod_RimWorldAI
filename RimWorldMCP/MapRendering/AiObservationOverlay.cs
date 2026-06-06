using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// AI 观察覆盖层 — 在地图上短暂显示半透明彩色填充矩形，告知玩家 AI 正在关注这些区域。
    /// 使用 GenDraw.DrawCellRect 单次绘制，O(1) 性能，零持久状态，天然自过期。
    /// </summary>
    public static class AiObservationOverlay
    {
        private const float DurationSec = 0.8f;
        private const float OverlayAlpha = 0.12f;

        private class Observation
        {
            public CellRect Rect;
            public Color Color;
            public float ExpireRealTime;
        }

        private static readonly Dictionary<Map, List<Observation>> _active = new Dictionary<Map, List<Observation>>();
        private static readonly Dictionary<Color, Material> _materialCache = new Dictionary<Color, Material>();

        /// <summary>
        /// 在地图上显示 AI 观察标记，~0.8 秒后自动消失（实时钟，不受暂停影响）。
        /// 颜色默认 Cyan（青色），与用户常用的白/红/蓝区分。
        /// </summary>
        public static void Show(Map map, CellRect rect, string label, string? colorName = null)
        {
            if (map == null || rect.IsEmpty) return;

            var color = ParseColor(colorName);

            if (!_active.TryGetValue(map, out var list))
            {
                list = new List<Observation>();
                _active[map] = list;
            }
            list.Add(new Observation
            {
                Rect = rect,
                Color = color,
                ExpireRealTime = Time.realtimeSinceStartup + DurationSec
            });
        }

        /// <summary>每帧调用——绘制活跃观察 + 清理过期项（纯实时钟，不受暂停影响）</summary>
        public static void Tick(Map? map)
        {
            if (map == null) return;
            if (!_active.TryGetValue(map, out var list)) return;

            var nowReal = Time.realtimeSinceStartup;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (nowReal >= list[i].ExpireRealTime)
                {
                    list.RemoveAt(i);
                    continue;
                }

                try
                {
                    var obs = list[i];
                    var mat = GetOrCreateMaterial(obs.Color);
                    GenDraw.DrawCellRect(obs.Rect, Vector3.zero, mat);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"[AiObservationOverlay] 绘制标记失败: {ex.Message}");
                }
            }

            if (list.Count == 0)
                _active.Remove(map);
        }

        private static Material GetOrCreateMaterial(Color color)
        {
            var key = new Color(color.r, color.g, color.b, OverlayAlpha);
            if (!_materialCache.TryGetValue(key, out var mat))
            {
                mat = SolidColorMaterials.SimpleSolidColorMaterial(key);
                _materialCache[key] = mat;
            }
            return mat;
        }

        private static Color ParseColor(string? colorName)
        {
            if (string.IsNullOrEmpty(colorName))
                return Color.cyan;

            switch (colorName!.ToLowerInvariant())
            {
                case "red":    return Color.red;
                case "green":  return Color.green;
                case "blue":   return Color.blue;
                case "yellow": return Color.yellow;
                case "magenta": return Color.magenta;
                case "white":  return Color.white;
                case "cyan":   return Color.cyan;
                case "orange": return new Color(1f, 0.6f, 0f);
                default:       return Color.cyan;
            }
        }
    }
}
