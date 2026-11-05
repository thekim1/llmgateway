using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Routing;
using Ume.LlmGateway.Gateway.Budgets;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>What one request (or live session) has collected on its way through the pipeline, for its usage record.</summary>
internal sealed class RequestState(string requestId, GatewayEndpoint endpoint, long startTimestamp)
{
    public string RequestId { get; } = requestId;
    public GatewayEndpoint Endpoint { get; } = endpoint;
    public long StartTimestamp { get; } = startTimestamp;
    public VirtualKey? Key { get; set; }
    public string? RequestedModel { get; set; }

    /// <summary>The request's residency ceiling (<see cref="GatewayHeaders.MaxResidency"/>); null = none.</summary>
    public DataResidency? MaxResidency { get; set; }
    public long EstimatedInputTokens { get; set; }
    public decimal EstimatedAudioSeconds { get; set; }
    public bool Streamed { get; set; }
    public int Fallbacks { get; set; }
    public PiiPolicy? PiiAction { get; set; }
    public string? PiiCategories { get; set; }

    /// <summary>What the content guard flagged (<c>request:…</c>, <c>answer:…</c>), for the usage record.</summary>
    public string? GuardFlags { get; set; }
    public BudgetReservation? Reservation { get; set; }
    public AppliedRule? Rule { get; set; }

    /// <summary>The deployment of the current (or last) attempt.</summary>
    public ModelDeployment? Deployment { get; set; }

    /// <summary>Set by the one <see cref="RequestAccounting.AccountAsync"/> call that counts.</summary>
    public bool Accounted { get; set; }

    /// <summary>
    /// <see cref="TimeProvider.GetTimestamp"/> ticks spent waiting on providers: every attempt, failed fallbacks included,
    /// and the relayed stream. Null when no provider was called (the request was refused first, or a live session).
    /// </summary>
    public long? UpstreamTicks { get; private set; }

    /// <summary>When the first stream event was sent to the client; 0 when none was (not streamed, or failed first).</summary>
    public long FirstEventTimestamp { get; set; }

    /// <summary>When the current (or last) attempt was sent to its provider.</summary>
    public long AttemptTimestamp { get; set; }

    /// <summary>When the first stream event arrived from the provider that answered; 0 when none did.</summary>
    public long ProviderFirstEventTimestamp { get; set; }

    public void AddUpstreamTime(long ticks) => UpstreamTicks = (UpstreamTicks ?? 0) + ticks;
}
