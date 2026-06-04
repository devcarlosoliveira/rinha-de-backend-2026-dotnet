using System.Buffers;
using Rinha.Api;
using Rinha.Core;

string indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? "artifacts/index.bin";
int nprobe = int.TryParse(Environment.GetEnvironmentVariable("NPROBE"), out int np) ? np : 16;
string port = Environment.GetEnvironmentVariable("PORT") ?? "9999";

// Carrega o índice ANTES de escutar — quando a porta responde, já está pronto.
Console.WriteLine($"carregando {indexPath} (nprobe={nprobe})...");
var index = IvfIndex.Load(indexPath);
Console.WriteLine($"índice pronto: N={index.N:N0} K={index.K}");

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.MapGet("/ready", (HttpContext ctx) =>
{
    ctx.Response.StatusCode = 200;
    return Task.CompletedTask;
});

app.MapPost("/fraud-score", async (HttpContext ctx) =>
{
    var (buf, len) = await ReadBody(ctx.Request);
    byte[] resp = Process(buf.AsSpan(0, len), index, nprobe);
    ArrayPool<byte>.Shared.Return(buf);

    var r = ctx.Response;
    r.StatusCode = 200;
    r.ContentType = "application/json";
    r.ContentLength = resp.Length;
    await r.Body.WriteAsync(resp);
});

app.Run();

// Processamento síncrono (permite stackalloc; não fica em método async).
static byte[] Process(ReadOnlySpan<byte> body, IvfIndex index, int nprobe)
{
    try
    {
        var fr = PayloadParser.Parse(body);
        Span<float> v = stackalloc float[Vectorizer.Dims];
        Vectorizer.Vectorize(fr, v);
        var res = index.Search(v, nprobe);
        int fc = (int)MathF.Round(res.Score * 5f);
        return Responses.ByFraudCount[fc];
    }
    catch
    {
        return Responses.Fallback;
    }
}

static async ValueTask<(byte[] buf, int len)> ReadBody(HttpRequest req)
{
    var body = req.Body;
    int cl = (int)(req.ContentLength ?? 0);

    if (cl > 0)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(cl);
        int read = 0;
        while (read < cl)
        {
            int r = await body.ReadAsync(buf.AsMemory(read, cl - read));
            if (r == 0) break;
            read += r;
        }
        return (buf, read);
    }

    // sem Content-Length (chunked): cresce conforme lê
    byte[] grow = ArrayPool<byte>.Shared.Rent(2048);
    int total = 0;
    while (true)
    {
        if (total == grow.Length)
        {
            byte[] nb = ArrayPool<byte>.Shared.Rent(grow.Length * 2);
            Array.Copy(grow, nb, total);
            ArrayPool<byte>.Shared.Return(grow);
            grow = nb;
        }
        int r = await body.ReadAsync(grow.AsMemory(total, grow.Length - total));
        if (r == 0) break;
        total += r;
    }
    return (grow, total);
}
