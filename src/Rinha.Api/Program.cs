using Rinha.Api;
using Rinha.Api.Epoll;
using Rinha.Core;

string indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "artifacts/index.bin";
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int p) ? p : 9999;
string? socketPath = Environment.GetEnvironmentVariable("SOCKET_PATH");
string runtime = Environment.GetEnvironmentVariable("RUNTIME") ?? "async"; // "epoll" | "async"

// Busca adaptativa: nLow buckets no passo barato; escala até nHigh nos casos ambíguos
// (não-unânimes). Default 8/128 — atinge o piso de recall a ~12 buckets/req em média.
int nLow = int.TryParse(Environment.GetEnvironmentVariable("NLOW"), out int nl) ? nl : 8;
int nHigh = int.TryParse(Environment.GetEnvironmentVariable("NHIGH"), out int nh) ? nh : 128;

// Em cgroup de 0.45 CPU o .NET enxerga 1 processador e o ThreadPool cresce só 1 thread/s —
// durante o ramp 1→900 rps do k6 isso enfileira e infla o p99. Pré-aquece um piso modesto
// de threads (sem oversubscribir a fração de CPU). Tunável via env.
{
    int minTh = int.TryParse(Environment.GetEnvironmentVariable("MIN_THREADS"), out int mt) ? mt : 8;
    ThreadPool.GetMinThreads(out _, out int minIo);
    ThreadPool.SetMinThreads(minTh, Math.Max(minIo, minTh));

    // Capa as worker threads: sob 0.45 CPU, um burst que processa N buscas em paralelo
    // estoura a quota do CFS e congela o container ~55ms (a maior fonte do p99). Serializar
    // o processamento mantém a CPU instantânea baixa ⇒ evita o throttle. 0/ausente = não mexe.
    if (int.TryParse(Environment.GetEnvironmentVariable("MAX_THREADS"), out int maxTh) && maxTh > 0)
    {
        ThreadPool.GetMaxThreads(out _, out int maxIo);
        ThreadPool.SetMaxThreads(Math.Max(maxTh, minTh), Math.Max(maxIo, maxTh));
    }
}

// Carrega o índice ANTES de escutar — quando a porta responde, já está pronto.
Console.WriteLine($"carregando {indexPath} (adaptativo nLow={nLow} nHigh={nHigh})...");
var index = IvfIndex.Load(indexPath);
Console.WriteLine($"índice pronto: N={index.N:N0} K={index.K} runtime={runtime} — escutando {(string.IsNullOrEmpty(socketPath) ? $":{port}" : socketPath)}");

// Trava todas as páginas (índice incluso) na RAM ⇒ zero page-fault no serviço (precisa de
// ulimit memlock). MCL_CURRENT também faz fault-in eager (pretouch). Tunável via env.
if (Environment.GetEnvironmentVariable("MLOCK_INDEX") == "1")
{
    if (Native.mlockall(Native.MCL_CURRENT | Native.MCL_FUTURE) == 0)
        Console.WriteLine("mlockall: índice travado na RAM");
    else
        Console.WriteLine("mlockall falhou (ulimit memlock?) — seguindo sem lock");
}

if (runtime == "epoll")
{
    // Event loop epoll mono-thread (pinado a um core via cpuset). Bloqueia para sempre.
    new EpollServer(index, nLow, nHigh).Run(socketPath, port);
    return;
}

// Servidor HTTP em socket cru assíncrono (Kestrel-free): bloqueia aqui para sempre.
await RawServer.RunAsync(port, socketPath, index, nLow, nHigh);
