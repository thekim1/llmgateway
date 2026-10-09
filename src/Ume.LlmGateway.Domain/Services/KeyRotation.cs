using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Services;

public static class KeyRotation
{
    public static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    public static IReadOnlySet<Guid> Ancestors(Guid keyId, IReadOnlyDictionary<Guid, Guid> replacements) =>
        Ancestors(keyId, PreviousKeys(replacements));

    /// <summary>Reverse of the replacement map (new key → keys it replaced), for repeated <see cref="Ancestors(Guid, ILookup{Guid, Guid})"/> calls.</summary>
    public static ILookup<Guid, Guid> PreviousKeys(IReadOnlyDictionary<Guid, Guid> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        return replacements.ToLookup(pair => pair.Value, pair => pair.Key);
    }

    public static IReadOnlySet<Guid> Ancestors(Guid keyId, ILookup<Guid, Guid> previousByReplacement)
    {
        ArgumentNullException.ThrowIfNull(previousByReplacement);
        var ids = new HashSet<Guid> { keyId };
        if (!previousByReplacement.Contains(keyId))
        {
            return ids;
        }

        var pending = new Queue<Guid>();
        pending.Enqueue(keyId);
        while (pending.TryDequeue(out var current))
        {
            foreach (var previous in previousByReplacement[current])
            {
                if (ids.Add(previous)) { pending.Enqueue(previous); }
            }
        }
        return ids;
    }

    public static IReadOnlySet<Guid> Descendants(Guid keyId, IReadOnlyDictionary<Guid, Guid> replacements)
    {
        var ids = new HashSet<Guid> { keyId };
        while (replacements.TryGetValue(keyId, out var replacement) && ids.Add(replacement))
        {
            keyId = replacement;
        }
        return ids;
    }

    /// <summary>
    /// Creates the replacement key (copying all policy settings) and retires the old key according to
    /// <paramref name="mode"/>. The caller persists both entities and shows <c>generated.PlainText</c> once.
    /// </summary>
    public static VirtualKey Rotate(VirtualKey oldKey, GeneratedKey generated, KeyRotationMode mode, DateTimeOffset now, string? actor)
    {
        ArgumentNullException.ThrowIfNull(oldKey);
        ArgumentNullException.ThrowIfNull(generated);

        if (!oldKey.IsUsable(now))
        {
            throw new InvalidOperationException("Only active keys can be rotated.");
        }

        var replacement = new VirtualKey
        {
            TeamId = oldKey.TeamId,
            Name = oldKey.Name,
            Description = oldKey.Description,
            Prefix = generated.Prefix,
            KeyHash = generated.Hash,
            CreatedAt = now,
            CreatedBy = actor,
            ExpiresAt = oldKey.ExpiresAt,
            AllowedModels = [.. oldKey.AllowedModels],
            AllowedResidencies = [.. oldKey.AllowedResidencies],
            AllowedProviders = [.. oldKey.AllowedProviders],
            PiiPolicy = oldKey.PiiPolicy,
            AttachmentPolicy = oldKey.AttachmentPolicy,
            RequestsPerMinute = oldKey.RequestsPerMinute,
            TokensPerMinute = oldKey.TokensPerMinute,
        };

        oldKey.RotatedToKeyId = replacement.Id;
        switch (mode)
        {
            case KeyRotationMode.Grace24Hours:
                oldKey.GraceUntil = now + GracePeriod;
                break;
            default:
                oldKey.RevokedAt = now;
                break;
        }

        return replacement;
    }

    public static void Revoke(VirtualKey key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.RevokedAt ??= now;
    }
}
