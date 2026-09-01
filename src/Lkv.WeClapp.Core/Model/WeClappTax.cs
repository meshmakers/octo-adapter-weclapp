namespace Lkv.WeClapp.Core.Model;

/// <summary>
/// A WeClapp <c>tax</c> entity (GET /tax), reduced to what the DILOS AI export reads: the id an
/// order position points at and the rate it is taxed with. The entity also carries a
/// <c>taxKey</c> and a <c>taxType</c>; neither reaches a DILOS file, because the AI contract
/// states the RATE in whole percent and not a tax key — the partner's key table maps 20 % to key
/// 6 and to key 20 alike, so a key would be ambiguous where the rate never is.
/// </summary>
public sealed record WeClappTax
{
    public string Id { get; init; } = "";

    /// <summary>The rate in percent, as a STRING in the API ("20", and with decimals: "13.5").
    /// </summary>
    public string? TaxValue { get; init; }
}
