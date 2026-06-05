using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Rinha.Core;

if (args.Length < 2)
{
    Console.WriteLine("uso: dotnet run -c Release -- <index.bin> <test-data.json> [--nprobe 4,8,16,32] [--limit N]");
    return 1;
}

string indexPath = args[0];
string testPath = args[1];
int[] nprobes = ParseList(args, "--nprobe", [4, 8, 16, 32]);
int limit = IntArg(args, "--limit", int.MaxValue);

Console.WriteLine("carregando índice...");
var index = IvfIndex.Load(indexPath);
Console.WriteLine($"  N={index.N:N0}  K={index.K}");

Console.WriteLine("carregando test-data...");
using var doc = JsonDocument.Parse(File.ReadAllBytes(testPath));
var entries = doc.RootElement.GetProperty("entries");
int total = Math.Min(entries.GetArrayLength(), limit);

var expected = new bool[total];
var qvecs = new float[total * Vectorizer.Dims];
{
    int i = 0;
    var swPv = Stopwatch.StartNew();
    foreach (var e in entries.EnumerateArray())
    {
        if (i >= total) break;
        expected[i] = e.GetProperty("expected_approved").GetBoolean();
        byte[] bytes = Encoding.UTF8.GetBytes(e.GetProperty("request").GetRawText());
        var fr = PayloadParser.Parse(bytes);
        Vectorizer.Vectorize(fr, qvecs.AsSpan(i * Vectorizer.Dims, Vectorizer.Dims));
        i++;
    }
    Console.WriteLine($"  {total:N0} payloads, parse+vectorize {swPv.Elapsed.TotalMilliseconds / total * 1000:F2} us/req\n");
}

// warmup (JIT)
for (int w = 0; w < Math.Min(2000, total); w++)
    index.Search(qvecs.AsSpan(w * Vectorizer.Dims, Vectorizer.Dims), 16);

Console.WriteLine($"{"nprobe",6} | {"FP",5} {"FN",5} | {"falhas%",8} {"acerto%",8} | {"detScore",9} | {"p50us",7} {"p99us",7}");
Console.WriteLine(new string('-', 78));

foreach (int nprobe in nprobes)
{
    int tp = 0, tn = 0, fp = 0, fn = 0;
    var times = new double[total];
    double tsToUs = 1e6 / Stopwatch.Frequency;

    for (int i = 0; i < total; i++)
    {
        long t0 = Stopwatch.GetTimestamp();
        var res = index.Search(qvecs.AsSpan(i * Vectorizer.Dims, Vectorizer.Dims), nprobe);
        times[i] = (Stopwatch.GetTimestamp() - t0) * tsToUs;

        bool exp = expected[i];
        if (res.Approved == exp) { if (exp) tn++; else tp++; }
        else { if (res.Approved) fn++; else fp++; }
    }

    Array.Sort(times);
    double p50 = times[total / 2];
    double p99 = times[Math.Min(total - 1, (int)(total * 0.99))];

    int n = tp + tn + fp + fn;
    int e = fp + 3 * fn;
    double failRate = (double)(fp + fn) / n;
    double eps = (double)e / n;
    double detScore = failRate > 0.15
        ? -3000
        : 1000 * Math.Log10(1 / Math.Max(eps, 0.001)) - 300 * Math.Log10(1 + e);
    double acerto = 100.0 * (tp + tn) / n;

    Console.WriteLine($"{nprobe,6} | {fp,5} {fn,5} | {failRate * 100,7:F2}% {acerto,7:F2}% | {detScore,9:F1} | {p50,7:F1} {p99,7:F1}");
}

// modo adaptativo: --adaptive nLow,nHigh[,nLow2,nHigh2,...] (pares)
int[] adaptive = ParseList(args, "--adaptive", []);
if (adaptive.Length >= 2)
{
    Console.WriteLine($"\n{"nLow",5} {"nHigh",6} | {"FP",5} {"FN",5} | {"detScore",9} | {"avgBkt",7} {"p50us",7} {"p99us",7}");
    Console.WriteLine(new string('-', 70));
    for (int k = 0; k + 1 < adaptive.Length; k += 2)
    {
        int nLow = adaptive[k], nHigh = adaptive[k + 1];
        int tp = 0, tn = 0, fp = 0, fn = 0;
        long sumScanned = 0;
        var times = new double[total];
        double tsToUs = 1e6 / Stopwatch.Frequency;
        for (int i = 0; i < total; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var res = index.SearchAdaptive(qvecs.AsSpan(i * Vectorizer.Dims, Vectorizer.Dims), nLow, nHigh, out int sc);
            times[i] = (Stopwatch.GetTimestamp() - t0) * tsToUs;
            sumScanned += sc;
            bool exp = expected[i];
            if (res.Approved == exp) { if (exp) tn++; else tp++; }
            else { if (res.Approved) fn++; else fp++; }
        }
        Array.Sort(times);
        int n = tp + tn + fp + fn;
        int e = fp + 3 * fn;
        double eps = (double)e / n;
        double failRate = (double)(fp + fn) / n;
        double detScore = failRate > 0.15 ? -3000 : 1000 * Math.Log10(1 / Math.Max(eps, 0.001)) - 300 * Math.Log10(1 + e);
        double avgBkt = (double)sumScanned / total;
        Console.WriteLine($"{nLow,5} {nHigh,6} | {fp,5} {fn,5} | {detScore,9:F1} | {avgBkt,7:F2} {times[total / 2],7:F1} {times[Math.Min(total - 1, (int)(total * 0.99))],7:F1}");
    }
}

return 0;

static int IntArg(string[] args, string name, int def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name && int.TryParse(args[i + 1], out int v)) return v;
    return def;
}

static int[] ParseList(string[] args, string name, int[] def)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1].Split(',').Select(int.Parse).ToArray();
    return def;
}
