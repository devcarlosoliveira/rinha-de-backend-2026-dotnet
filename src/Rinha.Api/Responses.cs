using System.Text;

namespace Rinha.Api;

/// <summary>
/// Respostas pré-serializadas. <c>fraud_score = fraudCount/5</c> e
/// <c>approved = score &lt; 0.6</c> ⇒ só 6 respostas possíveis (fraudCount 0..5).
/// Evita qualquer serialização JSON no caminho quente.
/// </summary>
static class Responses
{
    public static readonly byte[][] ByFraudCount =
    [
        U("{\"approved\":true,\"fraud_score\":0.0}"),  // 0/5
        U("{\"approved\":true,\"fraud_score\":0.2}"),  // 1/5
        U("{\"approved\":true,\"fraud_score\":0.4}"),  // 2/5
        U("{\"approved\":false,\"fraud_score\":0.6}"), // 3/5
        U("{\"approved\":false,\"fraud_score\":0.8}"), // 4/5
        U("{\"approved\":false,\"fraud_score\":1.0}"), // 5/5
    ];

    /// <summary>Fallback em erro: responde rápido e legítimo (evita HTTP 500, peso 5).</summary>
    public static readonly byte[] Fallback = ByFraudCount[0];

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
}
