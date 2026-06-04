using System.Runtime.CompilerServices;

namespace Rinha.Core;

/// <summary>
/// Transforma um <see cref="FraudRequest"/> no vetor de 14 dimensões
/// (ver ANALISE.md §7). Escreve em um <see cref="Span{T}"/> fornecido — sem alocação.
/// </summary>
public static class Vectorizer
{
    public const int Dims = 14;

    public static void Vectorize(in FraudRequest r, Span<float> v)
    {
        // 0 — amount
        v[0] = Clamp((float)(r.Amount / Normalization.MaxAmount));
        // 1 — installments
        v[1] = Clamp(r.Installments / Normalization.MaxInstallments);
        // 2 — amount_vs_avg (protege avg <= 0)
        double denom = r.CustomerAvgAmount * Normalization.AmountVsAvgRatio;
        v[2] = denom > 0 ? Clamp((float)(r.Amount / denom)) : 0f;
        // 3 — hour_of_day (UTC)
        v[3] = r.RequestedAtUtc.Hour / 23f;
        // 4 — day_of_week (seg=0 ... dom=6)
        int dow = ((int)r.RequestedAtUtc.DayOfWeek + 6) % 7;
        v[4] = dow / 6f;
        // 5 e 6 — desde a última transação (sentinela -1 quando não há)
        if (r.HasLastTx)
        {
            double minutes = (r.RequestedAtUtc - r.LastTxTimestampUtc).TotalMinutes;
            v[5] = Clamp((float)(minutes / Normalization.MaxMinutes));
            v[6] = Clamp((float)(r.LastKmFromCurrent / Normalization.MaxKm));
        }
        else
        {
            v[5] = -1f;
            v[6] = -1f;
        }
        // 7 — km_from_home
        v[7] = Clamp((float)(r.KmFromHome / Normalization.MaxKm));
        // 8 — tx_count_24h
        v[8] = Clamp(r.TxCount24h / Normalization.MaxTxCount24h);
        // 9 — is_online
        v[9] = r.IsOnline ? 1f : 0f;
        // 10 — card_present
        v[10] = r.CardPresent ? 1f : 0f;
        // 11 — unknown_merchant (1 = desconhecido)
        v[11] = IsKnown(r.MerchantId, r.KnownMerchants) ? 0f : 1f;
        // 12 — mcc_risk (padrão 0.5)
        v[12] = MccRisk.Get(r.Mcc);
        // 13 — merchant_avg_amount
        v[13] = Clamp((float)(r.MerchantAvgAmount / Normalization.MaxMerchantAvgAmount));
    }

    static bool IsKnown(string id, string[] known)
    {
        for (int i = 0; i < known.Length; i++)
            if (string.Equals(known[i], id, StringComparison.Ordinal))
                return true;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
}
