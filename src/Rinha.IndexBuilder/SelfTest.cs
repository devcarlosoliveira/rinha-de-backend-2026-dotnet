using Rinha.Core;

namespace Rinha.IndexBuilder;

/// <summary>
/// Valida o Vectorizer contra os dois exemplos completos da documentação
/// (REGRAS_DE_DETECCAO.md): uma transação legítima e uma fraudulenta.
/// </summary>
static class SelfTest
{
    public static int Run()
    {
        int fails = 0;

        // Exemplo legítimo (tx-1329056812)
        fails += Check("legit tx-1329056812", Legit(), new[]
        {
            0.0041f, 0.1667f, 0.05f, 0.7826f, 0.3333f, -1f, -1f,
            0.0292f, 0.15f, 0f, 1f, 0f, 0.15f, 0.006f,
        });

        // Exemplo fraudulento (tx-3330991687)
        fails += Check("fraud tx-3330991687", Fraud(), new[]
        {
            0.9506f, 0.8333f, 1.0f, 0.2174f, 0.8333f, -1f, -1f,
            0.9523f, 1.0f, 0f, 1f, 1f, 0.75f, 0.0055f,
        });

        Console.WriteLine(fails == 0 ? "\nSELFTEST PASS" : $"\nSELFTEST FAIL ({fails})");
        return fails == 0 ? 0 : 1;
    }

    static int Check(string name, FraudRequest r, float[] expected)
    {
        Span<float> v = stackalloc float[Vectorizer.Dims];
        Vectorizer.Vectorize(r, v);

        int bad = 0;
        for (int i = 0; i < Vectorizer.Dims; i++)
        {
            if (MathF.Abs(v[i] - expected[i]) > 1e-3f)
            {
                bad++;
                Console.WriteLine($"  dim{i,2}: obtido {v[i]:0.####}  esperado {expected[i]:0.####}");
            }
        }
        Console.WriteLine($"[{(bad == 0 ? "ok" : "XX")}] {name}");
        return bad == 0 ? 0 : 1;
    }

    static FraudRequest Legit() => new()
    {
        Amount = 41.12,
        Installments = 2,
        RequestedAtUtc = new DateTime(2026, 3, 11, 18, 45, 53, DateTimeKind.Utc),
        CustomerAvgAmount = 82.24,
        TxCount24h = 3,
        KnownMerchants = ["MERC-003", "MERC-016"],
        MerchantId = "MERC-016",
        Mcc = "5411",
        MerchantAvgAmount = 60.25,
        IsOnline = false,
        CardPresent = true,
        KmFromHome = 29.23,
        HasLastTx = false,
    };

    static FraudRequest Fraud() => new()
    {
        Amount = 9505.97,
        Installments = 10,
        RequestedAtUtc = new DateTime(2026, 3, 14, 5, 15, 12, DateTimeKind.Utc),
        CustomerAvgAmount = 81.28,
        TxCount24h = 20,
        KnownMerchants = ["MERC-008", "MERC-007", "MERC-005"],
        MerchantId = "MERC-068",
        Mcc = "7802",
        MerchantAvgAmount = 54.86,
        IsOnline = false,
        CardPresent = true,
        KmFromHome = 952.27,
        HasLastTx = false,
    };
}
