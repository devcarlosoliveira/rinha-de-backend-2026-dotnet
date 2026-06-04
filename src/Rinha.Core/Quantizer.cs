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

    /// <summary>
    /// Escala do int16: <c>round(x * 8000)</c>, uniforme em todas as dims (o sentinela -1
    /// das dims 5,6 vira -8000). Sem pesos ⇒ a distância int16 é a euclidiana pura escalada,
    /// igual ao ground truth. 8000 evita estouro de int32 na soma SIMD (máx ~20·8000² ≈
    /// 1,3e9 &lt; 2,1e9).
    /// </summary>
    public const float Scale16 = 8000f;

    public static short QuantizeI16(float x)
    {
        int q = (int)MathF.Round(x * Scale16, MidpointRounding.AwayFromZero);
        return (short)(q > short.MaxValue ? short.MaxValue : q < short.MinValue ? short.MinValue : q);
    }

    public static void QuantizeI16(ReadOnlySpan<float> v, Span<short> q)
    {
        for (int i = 0; i < v.Length; i++) q[i] = QuantizeI16(v[i]);
    }
}
