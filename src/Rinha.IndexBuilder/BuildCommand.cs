using System.Diagnostics;
using System.Runtime.InteropServices;
using Rinha.Core;

namespace Rinha.IndexBuilder;

/// <summary>Orquestra: carrega referências → k-means → reordena/quantiza → grava index.bin.</summary>
static class BuildCommand
{
    const int D = IndexFormat.Dims;

    public static int Run(string gzPath, string outPath, int k, int iters, int sampleSize, int seed)
    {
        if (!File.Exists(gzPath))
        {
            Console.Error.WriteLine($"arquivo não encontrado: {gzPath}");
            return 1;
        }

        var total = Stopwatch.StartNew();

        var sw = Stopwatch.StartNew();
        var data = ReferenceData.Load(gzPath);
        int n = data.Count;
        Console.WriteLine($"[load]   {n:N0} vetores em {sw.ElapsedMilliseconds} ms");

        // amostra para o treino
        sw.Restart();
        int sn = Math.Min(sampleSize, n);
        var sample = new float[sn * D];
        var rnd = new Random(seed);
        for (int i = 0; i < sn; i++)
            Array.Copy(data.Vectors, rnd.Next(n) * D, sample, i * D, D);
        var centroids = KMeans.Train(sample, sn, k, iters, seed);
        Console.WriteLine($"[kmeans] k={k} iters={iters} amostra={sn:N0} em {sw.ElapsedMilliseconds} ms");

        // atribui todos os N
        sw.Restart();
        var bucketOf = new int[n];
        KMeans.AssignAll(data.Vectors, n, centroids, k, bucketOf);
        Console.WriteLine($"[assign] {n:N0} vetores em {sw.ElapsedMilliseconds} ms");

        // contagem por bucket → offsets (prefix-sum)
        sw.Restart();
        var offsets = new int[k + 1];
        for (int i = 0; i < n; i++) offsets[bucketOf[i] + 1]++;
        int min = int.MaxValue, max = 0, empty = 0;
        for (int c = 0; c < k; c++)
        {
            int cnt = offsets[c + 1];
            if (cnt == 0) empty++;
            if (cnt < min) min = cnt;
            if (cnt > max) max = cnt;
        }
        for (int c = 0; c < k; c++) offsets[c + 1] += offsets[c];

        // reordena por bucket: quantiza vetores e monta o bitset de labels
        var cursor = (int[])offsets.Clone();
        var vectors16 = new short[n * D];
        var labels = new byte[(n + 7) / 8];
        for (int i = 0; i < n; i++)
        {
            int b = bucketOf[i];
            int pos = cursor[b]++;
            int src = i * D, dst = pos * D;
            for (int d = 0; d < D; d++)
                vectors16[dst + d] = Quantizer.QuantizeI16(data.Vectors[src + d]);
            if (data.Fraud[i] != 0)
                labels[pos >> 3] |= (byte)(1 << (pos & 7));
        }
        Console.WriteLine($"[bucket] min={min} max={max} avg={n / k:N0} vazios={empty} em {sw.ElapsedMilliseconds} ms");

        // grava index.bin
        sw.Restart();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using (var fs = File.Create(outPath))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(IndexFormat.Magic);
            bw.Write(IndexFormat.Version);
            bw.Write(D);
            bw.Write(n);
            bw.Write(k);
            bw.Write(MemoryMarshal.AsBytes(centroids.AsSpan()));
            bw.Write(MemoryMarshal.AsBytes(offsets.AsSpan()));
            bw.Write(labels);
            bw.Write(MemoryMarshal.AsBytes<short>(vectors16.AsSpan()));
        }
        long size = new FileInfo(outPath).Length;
        Console.WriteLine($"[write]  {outPath} ({size / (1024.0 * 1024.0):F1} MB) em {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"[done]   total {total.ElapsedMilliseconds} ms");
        return 0;
    }
}
