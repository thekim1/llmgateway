namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// Usage for one Europe/Stockholm month, key, model, provider and endpoint, kept after the month's usage records are
/// removed by the retention job. Written only by the database function <c>ume_retention_step</c>; never edited.
/// </summary>
public sealed class UsageMonthlyTotal
{
    public long Id { get; set; }

    /// <summary>The month's start (Europe/Stockholm midnight on the 1st) as an instant.</summary>
    public DateTimeOffset MonthStart { get; set; }

    public Guid VirtualKeyId { get; set; }
    public Guid TeamId { get; set; }
    public Guid DepartmentId { get; set; }
    public GatewayEndpoint Endpoint { get; set; }
    public required string RequestedModel { get; set; }
    public Guid? ProviderAccountId { get; set; }
    public string? ProviderName { get; set; }
    public Guid? ModelDeploymentId { get; set; }
    public long Requests { get; set; }
    public long SuccessfulRequests { get; set; }
    public long Fallbacks { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal AudioSeconds { get; set; }
    public decimal CostUsd { get; set; }
}
