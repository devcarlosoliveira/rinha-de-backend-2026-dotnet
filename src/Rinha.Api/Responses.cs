using System.Text;

namespace Rinha.Api;

/// <summary>
/// Respostas HTTP/1.1 <b>inteiras</b> pré-serializadas (status + headers + corpo), escritas
/// direto no socket num único send. <c>fraud_score = fraudCount/5</c> e
/// <c>approved = score &lt; 0.6</c> ⇒ só 6 corpos possíveis (fraudCount 0..5). Zero
/// serialização no caminho quente; sem Connection header ⇒ keep-alive default do HTTP/1.1.
/// </summary>
static class Responses
{
    static readonly string[] Bodies =
    [
        "{\"approved\":true,\"fraud_score\":0.0}",  // 0/5
        "{\"approved\":true,\"fraud_score\":0.2}",  // 1/5
        "{\"approved\":true,\"fraud_score\":0.4}",  // 2/5
        "{\"approved\":false,\"fraud_score\":0.6}", // 3/5
        "{\"approved\":false,\"fraud_score\":0.8}", // 4/5
        "{\"approved\":false,\"fraud_score\":1.0}", // 5/5
    ];

    /// <summary>Resposta HTTP completa por fraudCount (0..5).</summary>
    public static readonly byte[][] HttpByFraudCount =
    [
        Http(Bodies[0]), Http(Bodies[1]), Http(Bodies[2]),
        Http(Bodies[3]), Http(Bodies[4]), Http(Bodies[5]),
    ];

    /// <summary>Fallback em erro: 200 legítimo (evita não-200, que pesa 5× e conta no corte).</summary>
    public static readonly byte[] HttpFallback = HttpByFraudCount[0];

    /// <summary><c>GET /ready</c> → 200 vazio.</summary>
    public static readonly byte[] HttpReady =
        Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");

    static byte[] Http(string body) => Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
        + body.Length + "\r\n\r\n" + body);
}
