using System;
using System.Collections.Generic;

namespace RimWorldMCP.MapRendering
{
    public static class MapChunker
    {
        public static MapChunk GetChunkAt(int posX, int posZ,
            int mapWidth, int mapHeight, int chunkWidth, int chunkHeight)
        {
            int xIndex = posX / chunkWidth;
            int zIndex = posZ / chunkHeight;
            return BuildChunk(xIndex, zIndex, mapWidth, mapHeight, chunkWidth, chunkHeight);
        }

        public static MapChunk GetChunkByIndex(int xIndex, int zIndex,
            int mapWidth, int mapHeight, int chunkWidth, int chunkHeight)
        {
            return BuildChunk(xIndex, zIndex, mapWidth, mapHeight, chunkWidth, chunkHeight);
        }

        /// <summary>解析 chunk_id 字符串 "X_Z"，返回是否成功</summary>
        public static bool TryParseChunkId(string chunkId, out int xIndex, out int zIndex)
        {
            xIndex = 0; zIndex = 0;
            if (string.IsNullOrEmpty(chunkId)) return false;
            var parts = chunkId.Split('_');
            if (parts.Length != 2) return false;
            return int.TryParse(parts[0], out xIndex) && int.TryParse(parts[1], out zIndex);
        }

        /// <summary>chunk 索引格式化: XIndex_ZIndex</summary>
        public static string FormatChunkId(int xIndex, int zIndex) => $"{xIndex}_{zIndex}";

        /// <summary>获取矩形范围内覆盖的所有 chunk（行主序：Z 优先，X 次之）</summary>
        public static List<MapChunk> GetIntersectingChunks(
            int minX, int minZ, int maxX, int maxZ,
            int mapWidth, int mapHeight, int chunkWidth, int chunkHeight)
        {
            int firstCol = Math.Max(0, minX / chunkWidth);
            int lastCol = Math.Min((mapWidth - 1) / chunkWidth, maxX / chunkWidth);
            int firstRow = Math.Max(0, minZ / chunkHeight);
            int lastRow = Math.Min((mapHeight - 1) / chunkHeight, maxZ / chunkHeight);

            var chunks = new List<MapChunk>();
            for (int z = firstRow; z <= lastRow; z++)
                for (int x = firstCol; x <= lastCol; x++)
                    chunks.Add(BuildChunk(x, z, mapWidth, mapHeight, chunkWidth, chunkHeight));

            return chunks;
        }

        private static MapChunk BuildChunk(int xIndex, int zIndex,
            int mapWidth, int mapHeight, int chunkWidth, int chunkHeight)
        {
            int minX = xIndex * chunkWidth;
            int minZ = zIndex * chunkHeight;
            int maxX = Math.Min(minX + chunkWidth - 1, mapWidth - 1);
            int maxZ = Math.Min(minZ + chunkHeight - 1, mapHeight - 1);

            return new MapChunk
            {
                XIndex = xIndex,
                ZIndex = zIndex,
                MinX = minX,
                MinZ = minZ,
                MaxX = maxX,
                MaxZ = maxZ,
                Width = maxX - minX + 1,
                Height = maxZ - minZ + 1
            };
        }

        /// <summary>地图全部分块的网格维度（chunk数,列×行）</summary>
        public static (int cols, int rows) GetGridDimensions(
            int mapWidth, int mapHeight, int chunkWidth, int chunkHeight)
        {
            int cols = (mapWidth + chunkWidth - 1) / chunkWidth;
            int rows = (mapHeight + chunkHeight - 1) / chunkHeight;
            return (cols, rows);
        }
    }
}
