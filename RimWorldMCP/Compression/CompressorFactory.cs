namespace RimWorldMCP.Compression
{
    public static class CompressorFactory
    {
        public static IChunkCompressor Create(CompressionMethod method)
        {
            return method switch
            {
                CompressionMethod.Uncompressed => new UncompressedCompressor(),
                CompressionMethod.RLE => new RleCompressor(),
                CompressionMethod.RowRefRLE => new RowRefCompressor(),
                _ => new RleCompressor()
            };
        }
    }
}
