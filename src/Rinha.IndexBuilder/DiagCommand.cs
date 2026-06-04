using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rinha.Core;

namespace Rinha.IndexBuilder;

/// <summary>
/// Diagnóstico de detecção. No mesmo conjunto de queries amostradas, mede FP/FN de:
///   1. k-NN EXATO em float (euclidiana pura, sem pesos) — o ground truth da Rinha.
///   2. k-NN EXATO em int8 (métrica ponderada do nosso esquema) — isola a QUANTIZAÇÃO.
///   3. busca IVF int8 (nosso runtime) — quantização + RECALL.
/// Diferença (2 - 1) = custo de quantização; (3 - 2) = custo de recall do IVF.
/// </summary>
static class DiagCommand
{
    const int D = IndexFormat.Dims;
    static readonly int[] W = { 1, 1, 1, 1, 1, 4, 4, 1, 1, 1, 1, 1, 1, 1 };
    const int FULL = 54100; // p/ projetar detScore na escala do teste completo

    public static int Run(string gzPath, string indexPath, string testPath, int sample, int nprobe)
    {
        var sw = Stopwatch.StartNew();
        var data = ReferenceData.Load(gzPath);
        int n = data.Count;
        Console.WriteLine($"[refs]    {n:N0} vetores float em {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var q8refs = new byte[n * D];
        for (int i = 0; i < n * D; i++)
            q8refs[i] = Quantizer.QuantizeDim(data.Vectors[i], i % D);
        Console.WriteLine($"[int8]    refs quantizados em {sw.ElapsedMilliseconds} ms");

        var index = IvfIndex.Load(indexPath);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(testPath));
        var entries = doc.RootElement.GetProperty("entries");
        int total = Math.Min(entries.GetArrayLength(), sample);
        var qf = new float[total * D];
        var expected = new bool[total];
        int qi = 0;
        foreach (var e in entries.EnumerateArray())
        {
            if (qi >= total) break;
            expected[qi] = e.GetProperty("expected_approved").GetBoolean();
            var fr = PayloadParser.Parse(Encoding.UTF8.GetBytes(e.GetProperty("request").GetRawText()));
            Vectorizer.Vectorize(fr, qf.AsSpan(qi * D, D));
            qi++;
        }
        Console.WriteLine($"[queries] {total:N0} amostradas\n");

        var apFloat = new bool[total];
        var apInt8 = new bool[total];
        var apIvf = new bool[total];

        sw.Restart();
        Parallel.For(0, total, t =>
        {
            apFloat[t] = ExactFloat(qf, t, data.Vectors, data.Fraud, n);
            apInt8[t] = ExactInt8(qf, t, q8refs, data.Fraud, n);
        });
        for (int t = 0; t < total; t++)
            apIvf[t] = index.Search(qf.AsSpan(t * D, D), nprobe).Approved;
        Console.WriteLine($"[brute]   força bruta float+int8 + IVF em {sw.ElapsedMilliseconds} ms\n");

        Console.WriteLine($"{"método",-34} | {"FP",4} {"FN",4} | {"falhas%",8} {"acerto%",8} | {"detScore(full)",14}");
        Console.WriteLine(new string('-', 86));
        Report("1. k-NN EXATO float (ground truth)", apFloat, expected, total);
        Report("2. k-NN EXATO int8 (quantização)", apInt8, expected, total);
        Report($"3. IVF int8 nprobe={nprobe} (atual)", apIvf, expected, total);
        return 0;
    }

    static bool ExactFloat(float[] qf, int t, float[] refs, byte[] fraud, int n)
    {
        Span<float> bd = stackalloc float[5]; bd.Fill(float.MaxValue);
        Span<byte> bl = stackalloc byte[5]; bl.Clear();
        int qb = t * D;
        for (int i = 0; i < n; i++)
        {
            int rb = i * D;
            float s = 0f;
            for (int d = 0; d < D; d++) { float diff = qf[qb + d] - refs[rb + d]; s += diff * diff; }
            if (s < bd[4]) InsertF(bd, bl, s, fraud[i]);
        }
        return (bl[0] + bl[1] + bl[2] + bl[3] + bl[4]) / 5f < 0.6f;
    }

    static bool ExactInt8(float[] qf, int t, byte[] q8refs, byte[] fraud, int n)
    {
        Span<byte> qq = stackalloc byte[D];
        Quantizer.Quantize(qf.AsSpan(t * D, D), qq);
        Span<int> bd = stackalloc int[5]; bd.Fill(int.MaxValue);
        Span<byte> bl = stackalloc byte[5]; bl.Clear();
        for (int i = 0; i < n; i++)
        {
            int rb = i * D;
            int s = 0;
            for (int d = 0; d < D; d++) { int diff = qq[d] - q8refs[rb + d]; s += W[d] * diff * diff; }
            if (s < bd[4]) InsertI(bd, bl, s, fraud[i]);
        }
        return (bl[0] + bl[1] + bl[2] + bl[3] + bl[4]) / 5f < 0.6f;
    }

    static void InsertF(Span<float> d, Span<byte> l, float nd, byte nl)
    {
        d[4] = nd; l[4] = nl;
        for (int i = 4; i > 0 && d[i] < d[i - 1]; i--)
        { (d[i], d[i - 1]) = (d[i - 1], d[i]); (l[i], l[i - 1]) = (l[i - 1], l[i]); }
    }

    static void InsertI(Span<int> d, Span<byte> l, int nd, byte nl)
    {
        d[4] = nd; l[4] = nl;
        for (int i = 4; i > 0 && d[i] < d[i - 1]; i--)
        { (d[i], d[i - 1]) = (d[i - 1], d[i]); (l[i], l[i - 1]) = (l[i - 1], l[i]); }
    }

    static void Report(string name, bool[] ap, bool[] exp, int n)
    {
        int tp = 0, tn = 0, fp = 0, fn = 0;
        for (int i = 0; i < n; i++)
        {
            if (ap[i] == exp[i]) { if (exp[i]) tn++; else tp++; }
            else { if (ap[i]) fn++; else fp++; }
        }
        double eps = (double)(fp + 3 * fn) / n;
        double eFull = (fp + 3.0 * fn) * FULL / n; // E projetado p/ o teste completo
        double det = eps <= 0 ? 3000 : 1000 * Math.Log10(1 / Math.Max(eps, 0.001)) - 300 * Math.Log10(1 + eFull);
        Console.WriteLine($"{name,-34} | {fp,4} {fn,4} | {(double)(fp + fn) / n * 100,7:F2}% {(double)(tp + tn) / n * 100,7:F2}% | {det,14:F1}");
    }
}
