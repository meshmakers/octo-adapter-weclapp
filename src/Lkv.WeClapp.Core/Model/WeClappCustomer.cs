namespace Lkv.WeClapp.Core.Model;

/// <summary>WeClapp customer / party (subset relevant for the CK contact mapping).
/// DEFERRED (real account): the trial sample only carries the anonymous system debitor
/// with an empty address list — re-verify the addresses[] structure with real customer data.</summary>
public sealed record WeClappCustomer
{
    public string Id { get; init; } = "";
    public string CustomerNumber { get; init; } = "";
    public string Company { get; init; } = "";
    public string FirstName { get; init; } = "";
    public string LastName { get; init; } = "";
    public string Email { get; init; } = "";
    public string PartyType { get; init; } = "";
    public List<WeClappAddress> Addresses { get; init; } = new();
}
