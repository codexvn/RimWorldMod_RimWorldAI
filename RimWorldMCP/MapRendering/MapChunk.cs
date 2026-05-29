namespace RimWorldMCP.MapRendering
{
    public class MapChunk
    {
        public int XIndex { get; set; }
        public int ZIndex { get; set; }
        public int MinX { get; set; }
        public int MinZ { get; set; }
        public int MaxX { get; set; }
        public int MaxZ { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string CompressedData { get; set; } = "";
        public bool IsAllFog { get; set; }

        public override string ToString() =>
            $"Chunk({XIndex},{ZIndex}) [{MinX},{MinZ}]-[{MaxX},{MaxZ}] {Width}x{Height}";
    }
}
