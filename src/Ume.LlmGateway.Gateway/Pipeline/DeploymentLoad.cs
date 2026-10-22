using System.Collections.Concurrent;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>How the first target is chosen among targets of the same priority (<c>Gateway:LoadBalancing</c>).</summary>
public enum LoadBalancingMode
{
    /// <summary>Weighted random: each target gets its weight's share of first attempts.</summary>
    Weighted = 0,

    /// <summary>
    /// Power of two choices: two targets are drawn by weight and the one with the better <see cref="DeploymentLoad"/>
    /// score goes first, so a slow, failing or busy target gets less traffic. Weights then bias the draw, they no longer
    /// fix the shares.
    /// </summary>
    LeastLoaded = 1,
}

/// <summary>
/// What this instance has seen of each model deployment: moving averages (EWMA) of latency and success, and requests
/// in flight. The score follows agentgateway's: <c>success / (1 + latency × (1 + 0.1 × in flight))</c>, latency in
/// seconds. Local to the instance (no Redis): each instance balances its own traffic. A deployment without data scores
/// best, so a new one is tried.
/// </summary>
public sealed class DeploymentLoad
{
    private const double Alpha = 0.3;

    private readonly ConcurrentDictionary<Guid, Stats> _stats = new();

    /// <summary>Counts a request to <paramref name="deploymentId"/> as in flight until the lease is disposed.</summary>
    public Lease Start(Guid deploymentId)
    {
        var stats = _stats.GetOrAdd(deploymentId, static _ => new Stats());
        Interlocked.Increment(ref stats.InFlight);
        return new Lease(stats);
    }

    /// <summary>Higher is better; 1 for a deployment this instance has not used.</summary>
    public double Score(Guid deploymentId) =>
        _stats.TryGetValue(deploymentId, out var s)
            ? Volatile.Read(ref s.Health) / (1 + Volatile.Read(ref s.LatencySeconds) * (1 + 0.1 * Volatile.Read(ref s.InFlight)))
            : 1;

    internal sealed class Stats
    {
        public double Health = 1;
        public double LatencySeconds;
        public int InFlight;
    }

    /// <summary>One attempt. <see cref="Complete"/> records its outcome; <see cref="Dispose"/> ends it (after a stream, too).</summary>
    public sealed class Lease : IDisposable
    {
        private Stats? _stats;
        private bool _completed;

        internal Lease(Stats stats) => _stats = stats;

        /// <summary>Records how long the provider took to answer and whether that counts as healthy. Only the first call counts.</summary>
        public void Complete(bool healthy, TimeSpan latency)
        {
            if (_stats is not { } s || _completed)
            {
                return;
            }

            _completed = true;
            Record(ref s.Health, healthy ? 1 : 0);
            Record(ref s.LatencySeconds, latency.TotalSeconds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stats, null) is { } s)
            {
                Interlocked.Decrement(ref s.InFlight);
            }
        }

        private static void Record(ref double average, double value)
        {
            double seen, next;
            do
            {
                seen = Volatile.Read(ref average);
                next = seen == 0 ? value : Alpha * value + (1 - Alpha) * seen;
            }
            while (Interlocked.CompareExchange(ref average, next, seen) != seen);
        }
    }
}
