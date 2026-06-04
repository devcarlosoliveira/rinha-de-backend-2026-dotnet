namespace Rinha.Core;

/// <summary>
/// Quantização int8 (ver ANALISE.md §8):
///  - dims em [0,1]:            q = round(x * 255)
///  - dims 5 e 6 ({-1} ∪ [0,1]): q = round((x + 1) * 127.5)  → -1→0, 0→128, 1→255
/// Na distância, aplica-se peso 4 nas dims 5 e 6 (ver <see cref="DimWeight"/>).
/// </summary>
public static class Quantizer
{
    /// <summary>Peso por dimensão na distância euclidiana ao quadrado em espaço int8.</summary>
    public static readonly int[] DimWeight =
        { 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1, 1, 1, 1 };

    public static byte QuantizeDim(float x, int dim)
    {
        float scaled = (dim == 5 || dim == 6) ? (x + 1f) * 127.5f : x * 255f;
        int q = (int)MathF.Round(scaled, MidpointRounding.AwayFromZero);
        if (q < 0) q = 0;
        else if (q > 255) q = 255;
        return (byte)q;
    }

    public static void Quantize(ReadOnlySpan<float> v, Span<byte> q)
    {
        for (int i = 0; i < v.Length; i++)
            q[i] = QuantizeDim(v[i], i);
    }
}
