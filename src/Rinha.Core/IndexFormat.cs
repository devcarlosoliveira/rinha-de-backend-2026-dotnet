namespace Rinha.Core;

/// <summary>
/// Layout do <c>index.bin</c> (little-endian). Construído offline pelo IndexBuilder,
/// só lido no runtime. Ver ANALISE.md §9.
///
/// <code>
/// header : u32 magic | i32 version | i32 dims | i32 nVectors | i32 nCentroids
/// centroids : nCentroids * dims  float32
/// offsets   : (nCentroids + 1)   int32     (prefix-sum dos buckets)
/// vectors   : nVectors * dims    byte       (int8, REORDENADOS por bucket)
/// labels    : ceil(nVectors/8)   byte       (bit setado = fraude, ordem reordenada)
/// </code>
/// </summary>
public static class IndexFormat
{
    public const uint Magic = 0x484E4952; // 'R','I','N','H'
    public const int Version = 1;
    public const int Dims = 14;
}
