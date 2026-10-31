using System.Collections.Frozen;
using System.Net;
using Ume.LlmGateway.Domain.Networking;
using Ume.LlmGateway.Domain.Requests;

namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// A gateway API key handed to an application. Only an HMAC hash of the secret is stored;
/// the plaintext is shown once at creation/rotation.
/// </summary>
public sealed class VirtualKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Non-secret identifying prefix, e.g. <c>ume-sk-AbC1</c>, shown in UI and logs.</summary>
    public required string Prefix { get; set; }

    /// <summary>Hex HMAC-SHA256 of the full key using the server-side pepper.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Data-Protection-encrypted full key so gateway admins can reveal it later. Null for keys created before this was stored.</summary>
    public string? EncryptedSecret { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when the key was rotated with a grace period; key works until this instant.</summary>
    public DateTimeOffset? GraceUntil { get; set; }

    public Guid? RotatedToKeyId { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Allowed route aliases / model names. Empty = all.</summary>
    public List<string> AllowedModels { get; set; } = [];

    /// <summary>Allowed provider residencies. Empty = all.</summary>
    public List<DataResidency> AllowedResidencies { get; set; } = [];

    /// <summary>Allowed provider account names (<see cref="ProviderAccount.Name"/>). Empty = all providers.</summary>
    public List<string> AllowedProviders { get; set; } = [];

    /// <summary>
    /// Client addresses the key may be used from: single addresses or CIDR networks in canonical form
    /// (<see cref="Networking.NetworkRanges"/>). Empty = any address. Behind a reverse proxy this only works once the proxy is
    /// listed in <see cref="GatewaySettings.TrustedProxies"/>.
    /// </summary>
    public List<string> AllowedIpRanges { get; set; } = [];

    public PiiPolicy PiiPolicy { get; set; } = PiiPolicy.Off;
    public AttachmentPolicy AttachmentPolicy { get; set; } = AttachmentPolicy.Allowed;

    /// <summary>
    /// Whether requests may use tools the provider runs itself (web search, file search, code interpreter, MCP, web fetch),
    /// which send content to third parties or the internet. Off: only tools the client runs (<see cref="Providers.ProviderFeaturePolicy"/>).
    /// </summary>
    public bool AllowProviderTools { get; set; }

    /// <summary>
    /// Whether requests may ask the provider to keep the conversation (<c>store</c>, background responses, conversations).
    /// Off: refused, and Responses API requests are sent with <c>store: false</c>.
    /// </summary>
    public bool AllowProviderStorage { get; set; }

    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }

    /// <summary>Requests of this key in flight at once, across all gateway instances; null = no limit. Live audio sessions have their own limit.</summary>
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>Output-token cap, system prompt, parameter defaults and overrides; on top of the team's.</summary>
    public RequestPolicy? RequestPolicy { get; set; }

    /// <summary>Whether requests (and, when configured, answers) are checked by the on-prem content guard.</summary>
    public ContentGuardPolicy ContentGuardPolicy { get; set; }

    public KeyStatus GetStatus(DateTimeOffset now)
    {
        if (RevokedAt is { } revoked && revoked <= now)
        {
            return KeyStatus.Revoked;
        }

        if (!IsEnabled)
        {
            return KeyStatus.Disabled;
        }

        if (ExpiresAt is { } expires && expires <= now)
        {
            return KeyStatus.Expired;
        }

        if (GraceUntil is { } grace)
        {
            return grace > now ? KeyStatus.InGracePeriod : KeyStatus.Revoked;
        }

        return KeyStatus.Active;
    }

    public bool IsUsable(DateTimeOffset now) => GetStatus(now) is KeyStatus.Active or KeyStatus.InGracePeriod;

    // Case-insensitive index of AllowedModels and parsed AllowedIpRanges, built once by IndexAllowLists for keys held in a read-only cache.
    private FrozenSet<string>? _allowedModelIndex;
    private List<string>? _indexedModels;
    private int _indexedCount;
    private IPNetwork[]? _allowedNetworks;
    private List<string>? _indexedRanges;
    private int _indexedRangeCount;

    /// <summary>
    /// Builds the lookups <see cref="IsModelAllowed"/> and <see cref="IsAddressAllowed"/> use, so a cached key answers without
    /// allocating or parsing. Call once the key is loaded for read-only use; if a list is replaced or resized afterwards its
    /// index is ignored.
    /// </summary>
    public void IndexAllowLists()
    {
        _allowedModelIndex = AllowedModels.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _indexedModels = AllowedModels;
        _indexedCount = AllowedModels.Count;
        _allowedNetworks = NetworkRanges.ParseValid(AllowedIpRanges);
        _indexedRanges = AllowedIpRanges;
        _indexedRangeCount = AllowedIpRanges.Count;
    }

    /// <summary>
    /// The key's address allow-list. Empty = every address. An unknown address (no connection address) is refused when a
    /// list is set, and so is every address if no entry parses: a broken list must not open the key to everyone.
    /// </summary>
    public bool IsAddressAllowed(IPAddress? address)
    {
        if (AllowedIpRanges.Count == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        var networks = _allowedNetworks is { } index && ReferenceEquals(_indexedRanges, AllowedIpRanges) && _indexedRangeCount == AllowedIpRanges.Count
            ? index
            : NetworkRanges.ParseValid(AllowedIpRanges);
        return NetworkRanges.Contains(networks, address);
    }

    /// <summary>The key's model allow-list (route aliases and model names, case-insensitive). Empty = every model.</summary>
    public bool IsModelAllowed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (AllowedModels.Count == 0)
        {
            return true;
        }

        return _allowedModelIndex is { } index && ReferenceEquals(_indexedModels, AllowedModels) && _indexedCount == AllowedModels.Count
            ? index.Contains(name)
            : AllowedModels.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
