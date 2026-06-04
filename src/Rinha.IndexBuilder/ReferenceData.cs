using System.IO.Compression;
using System.Text.Json;
using Rinha.Core;

namespace Rinha.IndexBuilder;

/// <summary>Os 3M vetores de referência já normalizados, carregados em memória.</summary>
sealed class ReferenceData
{
    public required float[] Vectors;   // n * 14
    public required byte[] Fraud;      // n (1 = fraude)
    public required int Count;

    /// <summary>
    /// Descomprime o .gz inteiro para memória e parseia com Utf8JsonReader em
    /// uma passada. No build não há limite de recursos.
    /// </summary>
    public static ReferenceData Load(string gzPath, int capacity = 3_000_000)
    {
        byte[] json;
        int len;
        using (var fs = File.OpenRead(gzPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var ms = new MemoryStream(capacity: 320_000_000))
        {
            gz.CopyTo(ms, 1 << 20);
            json = ms.GetBuffer();
            len = (int)ms.Length;
        }

        const int D = IndexFormat.Dims;
        var vectors = new float[(long)capacity * D < int.MaxValue ? capacity * D : 0];
        if (vectors.Length == 0)
            throw new InvalidOperationException("capacity grande demais para um único array");
        var fraud = new byte[capacity];
        int n = 0;

        var reader = new Utf8JsonReader(json.AsSpan(0, len));
        Expect(ref reader, JsonTokenType.StartArray);

        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            if (n >= capacity)
            {
                Array.Resize(ref fraud, capacity * 2);
                Array.Resize(ref vectors, capacity * 2 * D);
                capacity *= 2;
            }

            int baseIdx = n * D;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("vector"))
                {
                    reader.Read(); // StartArray
                    for (int d = 0; d < D; d++)
                    {
                        reader.Read();
                        vectors[baseIdx + d] = reader.GetSingle();
                    }
                    reader.Read(); // EndArray
                }
                else if (reader.ValueTextEquals("label"))
                {
                    reader.Read();
                    fraud[n] = reader.ValueTextEquals("fraud") ? (byte)1 : (byte)0;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            n++;
        }

        if (n != capacity)
        {
            Array.Resize(ref vectors, n * D);
            Array.Resize(ref fraud, n);
        }

        return new ReferenceData { Vectors = vectors, Fraud = fraud, Count = n };
    }

    static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (!reader.Read() || reader.TokenType != type)
            throw new InvalidDataException($"esperava {type}, veio {reader.TokenType}");
    }
}
