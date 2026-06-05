using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Rinha.Core;

namespace Rinha.Api;

/// <summary>
/// Servidor HTTP/1.1 mínimo sobre <see cref="Socket"/> assíncrono — substitui o Kestrel
/// para tirar do caminho quente o overhead de framework/middleware/alocação por request
/// (o alvo é p99 ≤ 1ms sob 0.45 CPU). Aceita conexões keep-alive do nginx e, por conexão,
/// lê → roteia (POST /fraud-score vs GET) → escreve a resposta HTTP pré-serializada.
/// Robusto por desenho: qualquer falha de parse/socket vira fallback 200 ou fecha a conexão,
/// nunca um não-200 (que pesa 5× e conta no corte de 15%).
/// </summary>
static class RawServer
{
    public static async Task RunAsync(int port, IvfIndex index, int nLow, int nHigh)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Any, port));
        listener.Listen(512);

        while (true)
        {
            Socket conn;
            try { conn = await listener.AcceptAsync(); }
            catch { continue; }
            _ = HandleConnection(conn, index, nLow, nHigh); // fire-and-forget; multiplexa via async I/O
        }
    }

    static async Task HandleConnection(Socket sock, IvfIndex index, int nLow, int nHigh)
    {
        sock.NoDelay = true;
        byte[] buf = ArrayPool<byte>.Shared.Rent(8192);
        int len = 0; // bytes válidos em buf[0..len)
        try
        {
            while (true)
            {
                // 1. lê até o fim dos headers (\r\n\r\n)
                int bodyStart = IndexOfHeaderEnd(buf, 0, len);
                while (bodyStart < 0)
                {
                    if (len == buf.Length) buf = Grow(buf, len + 1);
                    int n = await sock.ReceiveAsync(buf.AsMemory(len), SocketFlags.None);
                    if (n <= 0) return; // conexão fechada
                    int from = len >= 3 ? len - 3 : 0; // o \r\n\r\n pode cruzar a fronteira da leitura
                    len += n;
                    bodyStart = IndexOfHeaderEnd(buf, from, len);
                }

                // 2. método + Content-Length
                bool isPost = buf[0] == (byte)'P';
                int contentLength = isPost ? ParseContentLength(buf, bodyStart) : 0;
                int total = bodyStart + contentLength;

                // 3. garante o corpo inteiro no buffer
                while (len < total)
                {
                    if (total > buf.Length) buf = Grow(buf, total);
                    int n = await sock.ReceiveAsync(buf.AsMemory(len), SocketFlags.None);
                    if (n <= 0) return;
                    len += n;
                }

                // 4. processa e responde
                byte[] resp = isPost
                    ? ProcessHttp(buf.AsSpan(bodyStart, contentLength), index, nLow, nHigh)
                    : Responses.HttpReady;
                await SendAll(sock, resp);

                // 5. consome `total` bytes (mantém pipeline/sobras p/ a próxima volta)
                int rem = len - total;
                if (rem > 0) Array.Copy(buf, total, buf, 0, rem);
                len = rem;
            }
        }
        catch { /* erro de socket: descarta a conexão silenciosamente */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            try { sock.Dispose(); } catch { }
        }
    }

    // Vetoriza + busca; retorna a resposta HTTP completa. Erro ⇒ fallback 200 (nunca 500).
    static byte[] ProcessHttp(ReadOnlySpan<byte> body, IvfIndex index, int nLow, int nHigh)
    {
        try
        {
            var fr = PayloadParser.Parse(body);
            Span<float> v = stackalloc float[Vectorizer.Dims];
            Vectorizer.Vectorize(fr, v);
            var res = index.SearchAdaptive(v, nLow, nHigh, out _);
            int fc = (int)MathF.Round(res.Score * 5f);
            return Responses.HttpByFraudCount[fc];
        }
        catch
        {
            return Responses.HttpFallback;
        }
    }

    static async ValueTask SendAll(Socket sock, byte[] data)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int n = await sock.SendAsync(data.AsMemory(sent), SocketFlags.None);
            if (n <= 0) throw new IOException("send falhou");
            sent += n;
        }
    }

    // Índice do byte logo após o primeiro \r\n\r\n em buf[from..len), ou -1.
    static int IndexOfHeaderEnd(byte[] b, int from, int len)
    {
        for (int i = from; i + 3 < len; i++)
            if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10)
                return i + 4;
        return -1;
    }

    static readonly byte[] CLKey = "content-length:"u8.ToArray();

    static int ParseContentLength(byte[] b, int headerEnd)
    {
        for (int i = 0; i + CLKey.Length <= headerEnd; i++)
        {
            if (!MatchCI(b, i, CLKey)) continue;
            int j = i + CLKey.Length;
            while (j < headerEnd && (b[j] == ' ' || b[j] == '\t')) j++;
            int val = 0;
            while (j < headerEnd && b[j] >= '0' && b[j] <= '9') { val = val * 10 + (b[j] - '0'); j++; }
            return val;
        }
        return 0;
    }

    // Compara b[off..] com `key` (já minúsculo) ignorando caixa.
    static bool MatchCI(byte[] b, int off, byte[] key)
    {
        for (int k = 0; k < key.Length; k++)
        {
            byte c = b[off + k];
            if (c >= 'A' && c <= 'Z') c = (byte)(c + 32);
            if (c != key[k]) return false;
        }
        return true;
    }

    static byte[] Grow(byte[] buf, int need)
    {
        int size = buf.Length * 2;
        while (size < need) size *= 2;
        byte[] nb = ArrayPool<byte>.Shared.Rent(size);
        Array.Copy(buf, nb, buf.Length);
        ArrayPool<byte>.Shared.Return(buf);
        return nb;
    }
}
