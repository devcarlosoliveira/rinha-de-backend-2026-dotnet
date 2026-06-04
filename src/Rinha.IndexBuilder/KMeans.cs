using Rinha.Core;

namespace Rinha.IndexBuilder;

/// <summary>
/// k-means (Lloyd) em espaço float — a mesma métrica euclidiana do avaliador,
/// com o sentinela -1 tratado como valor literal. Treina sobre uma amostra e
/// depois atribui todos os N vetores ao centroide mais próximo.
/// </summary>
static class KMeans
{
    const int D = IndexFormat.Dims;

    public static float[] Train(float[] sample, int sn, int k, int iters, int seed)
    {
        var rnd = new Random(seed);
        var cent = new float[k * D];
        for (int c = 0; c < k; c++)
            Array.Copy(sample, rnd.Next(sn) * D, cent, c * D, D);

        var assign = new int[sn];
        var sums = new float[k * D];
        var counts = new int[k];

        for (int it = 0; it < iters; it++)
        {
            Parallel.For(0, sn, i => assign[i] = Nearest(sample, i * D, cent, k));

            Array.Clear(sums);
            Array.Clear(counts);
            for (int i = 0; i < sn; i++)
            {
                int c = assign[i];
                int cb = c * D, sb = i * D;
                for (int d = 0; d < D; d++) sums[cb + d] += sample[sb + d];
                counts[c]++;
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0)
                {
                    Array.Copy(sample, rnd.Next(sn) * D, cent, c * D, D); // re-seed vazio
                    continue;
                }
                int cb = c * D;
                float inv = 1f / counts[c];
                for (int d = 0; d < D; d++) cent[cb + d] = sums[cb + d] * inv;
            }
        }

        return cent;
    }

    public static void AssignAll(float[] vec, int n, float[] cent, int k, int[] bucketOf)
    {
        Parallel.For(0, n, i => bucketOf[i] = Nearest(vec, i * D, cent, k));
    }

    static int Nearest(float[] data, int baseIdx, float[] cent, int k)
    {
        float best = float.MaxValue;
        int bi = 0;
        for (int c = 0; c < k; c++)
        {
            int cb = c * D;
            float s = 0f;
            for (int d = 0; d < D; d++)
            {
                float diff = data[baseIdx + d] - cent[cb + d];
                s += diff * diff;
            }
            if (s < best) { best = s; bi = c; }
        }
        return bi;
    }
}
