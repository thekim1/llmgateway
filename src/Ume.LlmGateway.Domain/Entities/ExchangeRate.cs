namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Left over from SEK costs (removed in 0.8.1): nothing reads or writes it. The table stays for one release so the
/// previous version keeps working during a rolling update, and is dropped in the next.
/// </summary>
public sealed class ExchangeRate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Currency { get; set; } = "USD";
    public decimal SekPerUnit { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
}
