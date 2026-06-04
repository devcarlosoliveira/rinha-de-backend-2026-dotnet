using System.Text.Json;

namespace Rinha.Core;

/// <summary>
/// Parser do payload <c>POST /fraud-score</c> (ver API.md) em <see cref="FraudRequest"/>.
/// Usa <see cref="Utf8JsonReader"/> diretamente sobre os bytes — AOT-friendly e sem
/// desserialização por reflexão. Compartilhado entre a API e o harness de validação.
/// </summary>
public static class PayloadParser
{
    public static FraudRequest Parse(ReadOnlySpan<byte> utf8)
    {
        var r = new FraudRequest
        {
            KnownMerchants = [],
            MerchantId = string.Empty,
            Mcc = null,
        };

        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("payload deve ser um objeto");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("transaction")) ParseTransaction(ref reader, ref r);
            else if (reader.ValueTextEquals("customer")) ParseCustomer(ref reader, ref r);
            else if (reader.ValueTextEquals("merchant")) ParseMerchant(ref reader, ref r);
            else if (reader.ValueTextEquals("terminal")) ParseTerminal(ref reader, ref r);
            else if (reader.ValueTextEquals("last_transaction")) ParseLastTransaction(ref reader, ref r);
            else { reader.Read(); reader.Skip(); } // id e desconhecidos
        }

        return r;
    }

    static void ParseTransaction(ref Utf8JsonReader reader, ref FraudRequest r)
    {
        reader.Read(); // StartObject
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("amount")) { reader.Read(); r.Amount = reader.GetDouble(); }
            else if (reader.ValueTextEquals("installments")) { reader.Read(); r.Installments = reader.GetInt32(); }
            else if (reader.ValueTextEquals("requested_at")) { reader.Read(); r.RequestedAtUtc = reader.GetDateTimeOffset().UtcDateTime; }
            else { reader.Read(); reader.Skip(); }
        }
    }

    static void ParseCustomer(ref Utf8JsonReader reader, ref FraudRequest r)
    {
        reader.Read(); // StartObject
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("avg_amount")) { reader.Read(); r.CustomerAvgAmount = reader.GetDouble(); }
            else if (reader.ValueTextEquals("tx_count_24h")) { reader.Read(); r.TxCount24h = reader.GetInt32(); }
            else if (reader.ValueTextEquals("known_merchants"))
            {
                reader.Read(); // StartArray
                List<string>? list = null;
                while (reader.Read() && reader.TokenType == JsonTokenType.String)
                    (list ??= []).Add(reader.GetString()!);
                if (list is not null) r.KnownMerchants = list.ToArray();
            }
            else { reader.Read(); reader.Skip(); }
        }
    }

    static void ParseMerchant(ref Utf8JsonReader reader, ref FraudRequest r)
    {
        reader.Read(); // StartObject
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("id")) { reader.Read(); r.MerchantId = reader.GetString() ?? string.Empty; }
            else if (reader.ValueTextEquals("mcc")) { reader.Read(); r.Mcc = reader.GetString(); }
            else if (reader.ValueTextEquals("avg_amount")) { reader.Read(); r.MerchantAvgAmount = reader.GetDouble(); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    static void ParseTerminal(ref Utf8JsonReader reader, ref FraudRequest r)
    {
        reader.Read(); // StartObject
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("is_online")) { reader.Read(); r.IsOnline = reader.GetBoolean(); }
            else if (reader.ValueTextEquals("card_present")) { reader.Read(); r.CardPresent = reader.GetBoolean(); }
            else if (reader.ValueTextEquals("km_from_home")) { reader.Read(); r.KmFromHome = reader.GetDouble(); }
            else { reader.Read(); reader.Skip(); }
        }
    }

    static void ParseLastTransaction(ref Utf8JsonReader reader, ref FraudRequest r)
    {
        reader.Read();
        if (reader.TokenType == JsonTokenType.Null) { r.HasLastTx = false; return; }

        r.HasLastTx = true; // StartObject
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("timestamp")) { reader.Read(); r.LastTxTimestampUtc = reader.GetDateTimeOffset().UtcDateTime; }
            else if (reader.ValueTextEquals("km_from_current")) { reader.Read(); r.LastKmFromCurrent = reader.GetDouble(); }
            else { reader.Read(); reader.Skip(); }
        }
    }
}
