using System.Collections.Concurrent;

namespace Ume.LlmGateway.AdminApi.Security;

/// <summary>
/// One <c>admin.access.denied</c> event per user and minute: the first refusal is logged at once, later ones in the same
/// minute are counted into the next event. Bounded, so a flood of users cannot grow it without limit.
/// </summary>
public sealed class AccessDeniedThrottle(TimeProvider time)
{
    private const int MaxUsers = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, Entry> _users = new(StringComparer.Ordinal);

    /// <summary>Whether to log this refusal now; <paramref name="count"/> is the refusals it stands for.</summary>
    public bool ShouldLog(string subject, out int count)
    {
        var now = time.GetUtcNow();
        if (_users.Count >= MaxUsers)
        {
            _users.Clear();
        }

        var entry = _users.GetOrAdd(subject, _ => new Entry());
        lock (entry)
        {
            entry.Pending++;
            if (now - entry.LoggedAt < Window)
            {
                count = 0;
                return false;
            }

            count = entry.Pending;
            entry.Pending = 0;
            entry.LoggedAt = now;
            return true;
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset LoggedAt { get; set; } = DateTimeOffset.MinValue;
        public int Pending { get; set; }
    }
}
