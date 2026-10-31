namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// Timing of one relayed stream after its first event: the longest wait for the provider's next event, the longest
/// time the gateway spent writing an event to the client, and how many waits were stalls. Separates a provider that
/// pauses from a gateway (or client) that falls behind. Kept as aggregates so the per-event cost is two timestamps.
/// </summary>
internal struct StreamGaps(TimeProvider time)
{
    /// <summary>A wait for the next event at least this long counts as a stall.</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(1);

    private readonly long _stallTicks = (long)(StallThreshold.TotalSeconds * time.TimestampFrequency);

    public long MaxUpstreamTicks { get; private set; }
    public long MaxWriteTicks { get; private set; }
    public int Stalls { get; private set; }
    public int Events { get; private set; }

    public readonly TimeSpan MaxUpstream => time.GetElapsedTime(0, MaxUpstreamTicks);
    public readonly TimeSpan MaxWrite => time.GetElapsedTime(0, MaxWriteTicks);

    /// <summary>Time from the previous event's write to this event's arrival: waiting on the provider.</summary>
    public void Upstream(long ticks)
    {
        MaxUpstreamTicks = Math.Max(MaxUpstreamTicks, ticks);
        if (ticks >= _stallTicks)
        {
            Stalls++;
        }
    }

    /// <summary>Time to encode and flush one event to the client.</summary>
    public void Write(long ticks)
    {
        MaxWriteTicks = Math.Max(MaxWriteTicks, ticks);
        Events++;
    }
}
