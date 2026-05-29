namespace RimWorldMCP.Compression
{
    public interface IChunkCompressor
    {
        string Name { get; }
        string Compress(char[][] rows, (int x, int z) chunkIndex);
    }
}
