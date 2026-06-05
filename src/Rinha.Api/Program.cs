using Rinha.Api;
using Rinha.Core;

string indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "artifacts/index.bin";
int nprobe = int.TryParse(Environment.GetEnvironmentVariable("NPROBE"), out int np) ? np : 16;
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int p) ? p : 9999;

// Em cgroup de 0.45 CPU o .NET enxerga 1 processador e o ThreadPool cresce só 1 thread/s —
// durante o ramp 1→900 rps do k6 isso enfileira e infla o p99. Pré-aquece um piso modesto
// de threads (sem oversubscribir a fração de CPU). Tunável via env.
{
    int minTh = int.TryParse(Environment.GetEnvironmentVariable("MIN_THREADS"), out int mt) ? mt : 8;
    ThreadPool.GetMinThreads(out _, out int minIo);
    ThreadPool.SetMinThreads(minTh, Math.Max(minIo, minTh));
}

// Carrega o índice ANTES de escutar — quando a porta responde, já está pronto.
Console.WriteLine($"carregando {indexPath} (nprobe={nprobe})...");
var index = IvfIndex.Load(indexPath);
Console.WriteLine($"índice pronto: N={index.N:N0} K={index.K} — escutando :{port}");

// Servidor HTTP em socket cru (sem Kestrel/ASP.NET): bloqueia aqui para sempre.
await RawServer.RunAsync(port, index, nprobe);
