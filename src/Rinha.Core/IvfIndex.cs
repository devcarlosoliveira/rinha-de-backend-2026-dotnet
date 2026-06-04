using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Rinha.Core;

public readonly record struct SearchResult(float Score, bool Approved);

/// <summary>
/// Índice IVF carregado de um <c>index.bin</c>. Read-only e thread-safe.
/// Consulta:
///  1. acha os <c>nprobe</c> centroides mais próximos (float32);
///  2. varre esses buckets em <b>int16</b> (escala <see cref="Quantizer.Scale16"/>) com
///     euclidiana <b>pura</b> em SIMD AVX2, mantendo os 5 vizinhos mais próximos.
/// int16 dá a precisão que casa com o k-NN exato float do avaliador (recupera o erro
/// de quantização do int8) e é SIMD-nativo (rápido); cabe em ~84 MB/instância.
/// </summary>
public sealed class IvfIndex
{
    const int D = IndexFormat.Dims;

    public int N { get; }
    public int K { get; }

    readonly float[] _centroids; // K * D
    readonly int[] _offsets;     // K + 1
    readonly byte[] _labels;     // bitset (fraude = 1)
    readonly short[] _vectors;   // N * D int16 (escala 8000), reordenado por bucket, + 16 padding

    IvfIndex(int n, int k, float[] cent, int[] off, byte[] lab, short[] vec)
    {
        N = n; K = k;
        _centroids = cent; _offsets = off; _labels = lab; _vectors = vec;
    }

    public static IvfIndex Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        if (br.ReadUInt32() != IndexFormat.Magic) throw new InvalidDataException("magic inválido");
        int version = br.ReadInt32();
        int dims = br.ReadInt32();
        int n = br.ReadInt32();
        int k = br.ReadInt32();
        if (version != IndexFormat.Version || dims != D)
            throw new InvalidDataException($"index incompatível (version={version}, dims={dims})");

        var cent = new float[k * D];
        ReadExact(br, MemoryMarshal.AsBytes(cent.AsSpan()));
        var off = new int[k + 1];
        ReadExact(br, MemoryMarshal.AsBytes(off.AsSpan()));
        var lab = new byte[(n + 7) / 8];
        ReadExact(br, lab);
        // +16 shorts de padding permite leitura SIMD de 16 shorts no último registro
        var vec = new short[n * D + 16];
        ReadExact(br, MemoryMarshal.AsBytes(vec.AsSpan(0, n * D)));

        return new IvfIndex(n, k, cent, off, lab, vec);
    }

    public SearchResult Search(ReadOnlySpan<float> q, int nprobe)
    {
        if (nprobe < 1) nprobe = 1;
        if (nprobe > K) nprobe = K;

        Span<short> qq = stackalloc short[16]; // 16 p/ SIMD; lanes 14,15 = 0
        Quantizer.QuantizeI16(q, qq.Slice(0, D));

        // --- etapa 1: nprobe centroides mais próximos (float), ascendentes ---
        Span<int> probe = stackalloc int[nprobe];
        Span<float> probeD = stackalloc float[nprobe];
        int filled = 0;
        for (int c = 0; c < K; c++)
        {
            float dc = CentroidDist(q, c);
            if (filled < nprobe)
            {
                int i = filled++;
                probeD[i] = dc; probe[i] = c;
                BubbleUp(probeD, probe, i);
            }
            else if (dc < probeD[nprobe - 1])
            {
                int i = nprobe - 1;
                probeD[i] = dc; probe[i] = c;
                BubbleUp(probeD, probe, i);
            }
        }

        // --- etapa 2: varre os buckets em int16 (euclidiana pura, SIMD), top-5 ---
        Span<int> bestD = stackalloc int[5];
        Span<byte> bestL = stackalloc byte[5];
        bestD.Fill(int.MaxValue);
        bestL.Clear();

        var vectors = _vectors;
        for (int pi = 0; pi < filled; pi++)
        {
            int b = probe[pi];
            int start = _offsets[b];
            int end = _offsets[b + 1];
            for (int v = start; v < end; v++)
            {
                int dist = VecDist(qq, vectors, v * D);
                if (dist < bestD[4])
                    InsertTop5(bestD, bestL, dist, Label(v));
            }
        }

        int fraud = bestL[0] + bestL[1] + bestL[2] + bestL[3] + bestL[4];
        float score = fraud / 5f;
        return new SearchResult(score, score < 0.6f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    float CentroidDist(ReadOnlySpan<float> q, int c)
    {
        int cb = c * D;
        var cent = _centroids;
        float s = 0f;
        for (int d = 0; d < D; d++)
        {
            float diff = q[d] - cent[cb + d];
            s += diff * diff;
        }
        return s;
    }

    // Máscara: lanes 0..13 = 1 (mantém), lanes 14,15 = 0 (zera o lixo lido a mais pelo SIMD).
    // Sem pesos por dimensão — a escala int16 é uniforme, então é euclidiana pura.
    static readonly Vector256<short> Mask =
        Vector256.Create((short)1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int VecDist(ReadOnlySpan<short> q, short[] vectors, int off)
    {
        if (Avx2.IsSupported)
        {
            Vector256<short> qs = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(q));
            Vector256<short> vs = Vector256.LoadUnsafe(ref vectors[off]);
            Vector256<short> diff = Avx2.MultiplyLow(Avx2.Subtract(qs, vs), Mask);
            return Vector256.Sum(Avx2.MultiplyAddAdjacent(diff, diff));
        }

        int s = 0;
        for (int d = 0; d < D; d++)
        {
            int diff = q[d] - vectors[off + d];
            s += diff * diff;
        }
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    byte Label(int v) => (byte)((_labels[v >> 3] >> (v & 7)) & 1);

    static void BubbleUp(Span<float> d, Span<int> idx, int i)
    {
        while (i > 0 && d[i] < d[i - 1])
        {
            (d[i], d[i - 1]) = (d[i - 1], d[i]);
            (idx[i], idx[i - 1]) = (idx[i - 1], idx[i]);
            i--;
        }
    }

    static void InsertTop5(Span<int> d, Span<byte> l, int nd, byte nl)
    {
        d[4] = nd; l[4] = nl;
        for (int i = 4; i > 0 && d[i] < d[i - 1]; i--)
        {
            (d[i], d[i - 1]) = (d[i - 1], d[i]);
            (l[i], l[i - 1]) = (l[i - 1], l[i]);
        }
    }

    static void ReadExact(BinaryReader br, Span<byte> dest)
    {
        int read = 0;
        while (read < dest.Length)
        {
            int r = br.Read(dest.Slice(read));
            if (r <= 0) throw new EndOfStreamException("index.bin truncado");
            read += r;
        }
    }
}
