using System.Collections.Frozen;
using System.Linq.Expressions;
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

    /// <summary>
    /// Every request must state its residency ceiling (<c>x-ume-max-residency</c>), or it is refused (400): a client
    /// whose code forgets the header fails instead of running without one. The ceiling never widens the key's residencies.
    /// </summary>
    public bool RequireMaxResidency { get; set; }

    public int? RequestsPerMinute { get; set; }
    public int? TokensPerMinute { get; set; }

    /// <summary>Requests of this key in flight at once, across all gateway instances; null = no limit. Live audio sessions have their own limit.</summary>
    public int? MaxConcurrentRequests { get; set; }

    /// <summary>Output-token cap, system prompt, parameter defaults and overrides; on top of the team's.</summary>
    public RequestPolicy? RequestPolicy { get; set; }

    /// <summary>Whether requests (and, when configured, answers) are checked by the on-prem content guard.</summary>
    public ContentGuardPolicy ContentGuardPolicy { get; set; }

    /// <summary>The integration the key belongs to (<see cref="IntegrationRole"/>); null for an ordinary key. Set at creation only.</summary>
    public Guid? IntegrationId { get; set; }
    public Integration? Integration { get; set; }

    /// <summary>The key's part in <see cref="Integration"/>: none, the platform's credential, or a key assigned to it.</summary>
    public IntegrationRole IntegrationRole { get; set; }

    /// <summary>What a credential key may do on the Integration API (<see cref="Keys.IntegrationScopes"/>). Empty = nothing.</summary>
    public List<string> IntegrationScopes { get; set; } = [];

    private FrozenDictionary<Guid, VirtualKey>? _assignedById;

    /// <summary>
    /// For a credential key held in the read-only key cache: the integration's assigned keys (not revoked), loaded with
    /// their teams and departments. Empty otherwise. Not stored.
    /// </summary>
    public IReadOnlyList<VirtualKey> AssignedKeys { get; private set; } = [];

    private FrozenSet<Guid>? _revokedAssignedIds;

    /// <summary>
    /// Holds <paramref name="keys"/> as <see cref="AssignedKeys"/>, indexed by id, and the ids of the integration's revoked
    /// assigned keys (only their ids, so a request naming one is told it is revoked); call once, when the key is loaded for read-only use.
    /// </summary>
    public void IndexAssignedKeys(IReadOnlyList<VirtualKey> keys, IEnumerable<Guid>? revokedIds = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        AssignedKeys = keys;
        _assignedById = keys.ToFrozenDictionary(k => k.Id);
        _revokedAssignedIds = revokedIds?.ToFrozenSet();
    }

    /// <summary>The assigned key with <paramref name="id"/> of this credential key's integration, or null.</summary>
    public VirtualKey? AssignedKey(Guid id) => _assignedById is { } index && index.TryGetValue(id, out var key) ? key : null;

    /// <summary>Whether <paramref name="id"/> is a revoked assigned key of this credential key's integration.</summary>
    public bool IsRevokedAssignedKey(Guid id) => _revokedAssignedIds?.Contains(id) == true;

    private List<DataResidency>? _ownResidencies;
    private bool _noCommonResidency;
    private IReadOnlyList<Department>? _organisation;

    /// <summary>The key's residencies as set on it: <see cref="AllowedResidencies"/> before <see cref="BoundBy"/> narrowed them.</summary>
    public IReadOnlyList<DataResidency> OwnAllowedResidencies => _ownResidencies ?? AllowedResidencies;

    /// <summary>After <see cref="BoundBy"/>: the credential key and this key allow no residency in common, so it cannot be used through that key.</summary>
    public bool NoCommonResidency => _noCommonResidency;

    /// <summary>
    /// For an assigned key held by <paramref name="credential"/> in the read-only key cache: narrows
    /// <see cref="AllowedResidencies"/> to those the credential key allows too, so a platform service never reaches a
    /// residency above its own key's (docs/integrations.md). Done once per load: requests read the narrowed list as any
    /// key's. A credential key without a list bounds nothing. When nothing is left the list is kept (empty would mean
    /// all) and <see cref="NoCommonResidency"/> is set. Call before <see cref="IndexAllowLists"/>.
    /// </summary>
    public void BoundBy(VirtualKey credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.AllowedResidencies.Count == 0)
        {
            return;
        }

        var bounded = AllowedResidencies.Count == 0
            ? [.. credential.AllowedResidencies]
            : AllowedResidencies.Where(credential.AllowedResidencies.Contains).ToList();
        if (bounded.Count == 0)
        {
            _noCommonResidency = true;
            return;
        }

        _ownResidencies = AllowedResidencies;
        AllowedResidencies = bounded;
    }

    /// <summary>
    /// For a credential key with the scope <see cref="Keys.IntegrationScopes.OrganisationRead"/> held in the read-only
    /// key cache: every department with its teams, for <c>/v1/ume/context</c>. Null otherwise. Not stored.
    /// </summary>
    public IReadOnlyList<Department>? Organisation => _organisation;

    public void HoldOrganisation(IReadOnlyList<Department> departments) => _organisation = departments;

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

    /// <summary>
    /// <see cref="GetStatus"/> as a query condition, so a list filtered by status is filtered in the database. Keep the two in
    /// step (<c>KeyStatusAndRotationTests</c> compares them for every combination of the fields).
    /// </summary>
    public static Expression<Func<VirtualKey, bool>> HasStatus(KeyStatus status, DateTimeOffset now) => status switch
    {
        KeyStatus.Revoked => k => (k.RevokedAt != null && k.RevokedAt <= now)
            || (k.IsEnabled && (k.ExpiresAt == null || k.ExpiresAt > now) && k.GraceUntil != null && k.GraceUntil <= now),
        KeyStatus.Disabled => k => (k.RevokedAt == null || k.RevokedAt > now) && !k.IsEnabled,
        KeyStatus.Expired => k => (k.RevokedAt == null || k.RevokedAt > now) && k.IsEnabled && k.ExpiresAt != null && k.ExpiresAt <= now,
        KeyStatus.InGracePeriod => k => (k.RevokedAt == null || k.RevokedAt > now) && k.IsEnabled && (k.ExpiresAt == null || k.ExpiresAt > now)
            && k.GraceUntil != null && k.GraceUntil > now,
        KeyStatus.Active => k => (k.RevokedAt == null || k.RevokedAt > now) && k.IsEnabled && (k.ExpiresAt == null || k.ExpiresAt > now)
            && k.GraceUntil == null,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown key status."),
    };

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
