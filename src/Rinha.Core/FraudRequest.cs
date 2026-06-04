namespace Rinha.Core;

/// <summary>
/// Campos crus de um payload <c>POST /fraud-score</c>, já parseados.
/// Mantém o Core desacoplado do parsing JSON.
/// </summary>
public struct FraudRequest
{
    public double Amount;
    public int Installments;
    public DateTime RequestedAtUtc;

    public double CustomerAvgAmount;
    public int TxCount24h;
    public string[] KnownMerchants;

    public string MerchantId;
    public string? Mcc;
    public double MerchantAvgAmount;

    public bool IsOnline;
    public bool CardPresent;
    public double KmFromHome;

    /// <summary><c>false</c> quando <c>last_transaction == null</c> (dims 5 e 6 viram -1).</summary>
    public bool HasLastTx;
    public DateTime LastTxTimestampUtc;
    public double LastKmFromCurrent;
}
