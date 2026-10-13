using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Mcp;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Limits of MCP server settings, shared by the API and the configuration document.</summary>
public static partial class McpServerLimits
{
    public const int MaxHeaders = 20;
    public const int MaxHeaderNameLength = 100;
    public const int MaxCredentialLength = 2000;
    public const int MaxResultBytes = 16 * 1024 * 1024;

    /// <summary>Headers a server's static credential may not set: they belong to HTTP or the MCP transport.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-type", "content-length", "transfer-encoding", "connection", "keep-alive", "proxy-connection", "te", "trailer",
        "upgrade", "accept", "last-event-id", "mcp-session-id", "mcp-protocol-version", "mcp-method", "mcp-name",
    };

    public static bool IsHeaderName(string name) => name.Length is > 0 and <= MaxHeaderNameLength && HeaderToken().IsMatch(name);

    public static bool IsStaticHeader(string name) => IsHeaderName(name) && !ReservedHeaders.Contains(name);

    /// <summary>
    /// Why <paramref name="address"/> cannot be an MCP server or token URL, or null: the providers' rule (absolute http(s), no
    /// credentials, fragment or secret query parameters; plain HTTP only for on-prem).
    /// </summary>
    public static string? AddressProblem(string? address, DataResidency residency) =>
        !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Fragment) || (uri.Scheme == "http" && residency != DataResidency.OnPrem)
            ? "Ange en HTTPS-adress utan inloggningsuppgifter eller fragment. HTTP är endast tillåtet för on-prem."
            : ProviderRequest.HasSecretParameter(uri.Query) ? "Frågesträngen får inte innehålla nycklar eller token. Ange dem som autentiseringsuppgift i stället."
            : null;

    [GeneratedRegex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderToken();
}

/// <summary>
/// A write-only MCP server credential. <c>Headers</c> for <see cref="McpAuthType.StaticHeaders"/>; <c>TokenUrl</c>,
/// <c>ClientId</c>, <c>ClientSecret</c> and <c>Scope</c> for <see cref="McpAuthType.OAuthClientCredentials"/>.
/// </summary>
public sealed record McpCredentialRequest(
    Dictionary<string, string>? Headers = null, string? TokenUrl = null, string? ClientId = null, string? ClientSecret = null, string? Scope = null)
{
    /// <summary>The credential as the gateway reads it (<see cref="McpUpstreamAuth.Parse"/>).</summary>
    public string Plaintext(McpAuthType type) => type == McpAuthType.StaticHeaders
        ? McpUpstreamAuth.StaticHeadersCredential(Headers ?? [])
        : McpUpstreamAuth.ClientCredentialsCredential(TokenUrl ?? "", ClientId ?? "", ClientSecret ?? "", Scope);
}

public sealed record McpServerRequest(
    [property: Required, StringLength(McpToolNames.MaxSlugLength)] string Slug,
    [property: Required, StringLength(200)] string DisplayName,
    [property: Required, StringLength(500)] string Url,
    [property: EnumDataType(typeof(DataResidency))] DataResidency Residency,
    [property: EnumDataType(typeof(McpAuthType))] McpAuthType AuthType = McpAuthType.None,
    [property: StringLength(1000)] string? Description = null,
    bool IsEnabled = true,
    Guid? OwnerDepartmentId = null,
    [property: MaxLength(McpServerLimits.MaxHeaders)] string[]? ForwardHeaders = null,
    [property: Range(1, 900)] int TimeoutSeconds = 30,
    [property: Range(1024, McpServerLimits.MaxResultBytes)] int MaxResultBytes = 1024 * 1024,
    bool AutoApproveNewTools = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpCredentialRequest? Credential = null) : AdminRequest, IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Slug is not null && !McpToolNames.IsValidSlug(Slug))
        {
            yield return new ValidationResult(
                $"Ange små bokstäver och siffror, med enkla understreck emellan, som börjar med en bokstav (högst {McpToolNames.MaxSlugLength} tecken).", [nameof(Slug)]);
        }
        if (McpServerLimits.AddressProblem(Url, Residency) is { } address)
        {
            yield return new ValidationResult(address, [nameof(Url)]);
        }
        if (ForwardHeaders is not null && (ForwardHeaders.Any(h => h is null || !McpServerLimits.IsHeaderName(h) || !McpUpstream.IsForwardable(h)) ||
            ForwardHeaders.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ForwardHeaders.Length))
        {
            yield return new ValidationResult(
                "Ange giltiga, unika rubriknamn. Authorization, nycklar, cookies och HTTP- eller MCP-protokollrubriker vidarebefordras aldrig.", [nameof(ForwardHeaders)]);
        }
        if (CredentialProblem() is { } credential)
        {
            yield return new ValidationResult(credential, [nameof(Credential)]);
        }
    }

    private string? CredentialProblem()
    {
        if (Credential is not { } c)
        {
            return null;
        }

        switch (AuthType)
        {
            case McpAuthType.None:
                return "Ange ingen autentiseringsuppgift när servern inte kräver autentisering.";
            case McpAuthType.StaticHeaders:
                if (c.Headers is not { Count: > 0 and <= McpServerLimits.MaxHeaders } headers ||
                    headers.Any(h => !McpServerLimits.IsStaticHeader(h.Key) || string.IsNullOrEmpty(h.Value) || h.Value.Any(char.IsControl)) ||
                    headers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count)
                {
                    return $"Ange 1–{McpServerLimits.MaxHeaders} rubriker med giltiga, unika namn och värden utan radbrytningar. HTTP- och MCP-protokollrubriker kan inte sättas.";
                }

                break;
            default:
                if (string.IsNullOrWhiteSpace(c.ClientId) || string.IsNullOrWhiteSpace(c.ClientSecret))
                {
                    return "Ange klient-id och klienthemlighet.";
                }
                if (McpServerLimits.AddressProblem(c.TokenUrl, Residency) is { } tokenUrl)
                {
                    return "Tokenadressen: " + tokenUrl;
                }

                break;
        }

        return c.Plaintext(AuthType).Length > McpServerLimits.MaxCredentialLength ? "Autentiseringsuppgiften är för lång." : null;
    }
}

public sealed record ApproveToolRequest([property: Required, StringLength(64)] string DefinitionHash) : AdminRequest;

public sealed record ToolPriceRequest([property: Range(typeof(decimal), "0", "1000000")] decimal? PriceSek) : AdminRequest;

/// <summary>MCP servers and their tools. Gateway admins manage every server; department admins their departments' servers.</summary>
public sealed class McpServerService(AdminContext ctx, CredentialProtector protector, McpUrlGuard guard, IHostEnvironment environment, McpProbe probe)
{
    public const string ManageDenied = "Globala MCP-servrar och deras verktyg hanteras av en gatewayadministratör.";

    /// <summary>Servers the user may see: every server for gateway admins, otherwise global servers and the user's departments' servers.</summary>
    public IQueryable<McpServer> Visible(ClaimsPrincipal user) => AdminContext.IsAdmin(user)
        ? ctx.Db.McpServers
        : ctx.Db.McpServers.Where(s => s.OwnerDepartmentId == null || ctx.Departments(user).Any(d => d.Id == s.OwnerDepartmentId));

    public async Task<List<McpServerDto>> ListAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var servers = await Visible(user).AsNoTracking().OrderBy(s => s.Slug).ToListAsync(ct);
        return await DtosAsync(user, servers, ct);
    }

    public async Task<McpServerDto> GetAsync(ClaimsPrincipal user, Guid id, CancellationToken ct) =>
        (await DtosAsync(user, [await FindAsync(user, id, ct)], ct))[0];

    public async Task<McpServer> FindAsync(ClaimsPrincipal user, Guid id, CancellationToken ct) =>
        await Visible(user).SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new ApiFaultException(404, "MCP-servern finns inte.");

    /// <summary>The server, when the user may change it (403 for a server the user can only see).</summary>
    public async Task<McpServer> ManageableAsync(ClaimsPrincipal user, Guid id, CancellationToken ct, bool withTools = false)
    {
        var query = Visible(user);
        var server = await (withTools ? query.Include(s => s.Tools) : query).SingleOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiFaultException(404, "MCP-servern finns inte.");
        return AdminContext.IsAdmin(user) || server.OwnerDepartmentId is not null ? server : throw new ApiFaultException(403, ManageDenied);
    }

    public async Task<McpServerDto> CreateAsync(ClaimsPrincipal user, McpServerRequest input, CancellationToken ct)
    {
        await EnsureOwnerAsync(user, input.OwnerDepartmentId, ct);
        await EnsureSlugFreeAsync(input.Slug, null, ct);
        await CheckAddressesAsync(user, input, null, ct);
        var s = new McpServer { Slug = input.Slug, DisplayName = input.DisplayName, Url = input.Url, CreatedAt = ctx.Now };
        Apply(s, input, credentialRequired: true);
        ctx.Db.McpServers.Add(s);
        await ctx.SaveAsync(user, AuditActions.Create, AuditEntities.McpServer, s.Id, null, McpServerAudit.Of(s), InvalidationKind.Config, ct);
        return await GetAsync(user, s.Id, ct);
    }

    public async Task<McpServerDto> UpdateAsync(ClaimsPrincipal user, Guid id, McpServerRequest input, CancellationToken ct)
    {
        var s = await ManageableAsync(user, id, ct, withTools: true);
        await EnsureOwnerAsync(user, input.OwnerDepartmentId, ct);
        await EnsureSlugFreeAsync(input.Slug, id, ct);
        await CheckAddressesAsync(user, input, s, ct);
        var before = McpServerAudit.Of(s);
        Apply(s, input, credentialRequired: true);
        var after = McpServerAudit.Of(s) with { CredentialReplaced = input.Credential is not null && s.EncryptedCredential is not null };
        await ctx.SaveAsync(user, AuditActions.Update, AuditEntities.McpServer, id, before, after, InvalidationKind.Config, ct);
        return await GetAsync(user, id, ct);
    }

    /// <summary>Deletes the server with its tools and every key's grant on it.</summary>
    public async Task DeleteAsync(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var s = await ManageableAsync(user, id, ct);
        ctx.Db.McpServers.Remove(s);
        await ctx.SaveAsync(user, AuditActions.Delete, AuditEntities.McpServer, id, McpServerAudit.Of(s), null, InvalidationKind.Config, ct);
    }

    /// <summary>
    /// Copies the request onto the server. The credential is replaced when one is given and dropped when the server needs
    /// none; otherwise the stored one is kept if it is for the same auth type and the URL keeps its scheme, host and port, so
    /// a stored secret is never sent to a host the person who entered it did not choose. A new slug renames the server's tools on <c>/mcp</c>.
    /// </summary>
    public void Apply(McpServer s, McpServerRequest input, bool credentialRequired)
    {
        if (input.AuthType == McpAuthType.None)
        {
            s.EncryptedCredential = null;
        }
        else if (input.Credential is { } credential)
        {
            var plaintext = credential.Plaintext(input.AuthType);
            try
            {
                _ = McpUpstreamAuth.Parse(input.AuthType, plaintext);
            }
            catch (FormatException)
            {
                throw Invalid("credential", "Autentiseringsuppgiften har fel form.");
            }

            s.EncryptedCredential = protector.Protect(plaintext);
        }
        else if (s.AuthType != input.AuthType || s.EncryptedCredential is null || !SameOrigin(s.Url, input.Url))
        {
            if (credentialRequired)
            {
                throw Invalid("credential", "Ange autentiseringsuppgifter för den valda autentiseringstypen.");
            }

            s.EncryptedCredential = null;
        }

        if (s.Slug != input.Slug)
        {
            foreach (var tool in s.Tools)
            {
                tool.ExposedName = McpToolNames.Exposed(input.Slug, tool.Name);
            }
        }

        s.Slug = input.Slug; s.DisplayName = input.DisplayName.Trim(); s.Description = input.Description; s.Url = input.Url;
        s.Residency = input.Residency; s.IsEnabled = input.IsEnabled; s.OwnerDepartmentId = input.OwnerDepartmentId; s.AuthType = input.AuthType;
        s.ForwardHeaders = [.. input.ForwardHeaders ?? []]; s.TimeoutSeconds = input.TimeoutSeconds; s.MaxResultBytes = input.MaxResultBytes;
        s.AutoApproveNewTools = input.AutoApproveNewTools;
    }

    private static bool SameOrigin(string stored, string requested) =>
        Uri.TryCreate(stored, UriKind.Absolute, out var a) && Uri.TryCreate(requested, UriKind.Absolute, out var b)
        && Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The server's decrypted credential; a credential that cannot be read gives null.</summary>
    public McpUpstreamAuth? Auth(McpServer s)
    {
        try
        {
            return s.AuthType == McpAuthType.None ? McpUpstreamAuth.None : McpUpstreamAuth.Parse(s.AuthType, protector.Unprotect(s.EncryptedCredential));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Connects to a server and lists its tools without saving anything: the stored server (<paramref name="id"/>), the
    /// stored server with the changes in <paramref name="input"/>, or a new one (<paramref name="input"/> only).
    /// </summary>
    public async Task<McpTestResultDto> TestAsync(ClaimsPrincipal user, Guid? id, McpServerRequest? input, CancellationToken ct)
    {
        McpServer server;
        if (id is { } existing)
        {
            var stored = await ManageableAsync(user, existing, ct);
            server = new McpServer
            {
                Id = stored.Id, Slug = stored.Slug, DisplayName = stored.DisplayName, Url = stored.Url, Residency = stored.Residency,
                AuthType = stored.AuthType, EncryptedCredential = stored.EncryptedCredential, TimeoutSeconds = stored.TimeoutSeconds,
                MaxResultBytes = stored.MaxResultBytes,
            };
            if (input is not null)
            {
                await CheckAddressesAsync(user, input, stored, ct);
                Apply(server, input, credentialRequired: true);
            }
        }
        else
        {
            if (input is null)
            {
                throw new ApiFaultException(400, "Ange servern som ska testas.");
            }

            await EnsureOwnerAsync(user, input.OwnerDepartmentId, ct);
            await CheckAddressesAsync(user, input, null, ct);
            server = new McpServer { Slug = input.Slug, DisplayName = input.DisplayName, Url = input.Url };
            Apply(server, input, credentialRequired: true);
        }

        if (Auth(server) is not { } auth)
        {
            return new McpTestResultDto(false, McpProbe.CredentialUnreadable, null, [], null, [], []);
        }

        if (await guard.CheckAsync(new Uri(server.Url), allowPrivate: true, ct) is { } refused)
        {
            return new McpTestResultDto(false, refused, null, [], null, [], []);
        }

        var result = await probe.ProbeAsync(server, auth, ct);
        return new McpTestResultDto(
            result.Outcome.Succeeded, result.Outcome.Succeeded ? null : McpProbe.Describe(result.Outcome), result.Outcome.HttpStatus,
            McpProbe.Flags(result.Capabilities), result.Instructions,
            [.. result.Tools.Select(t => Summary(server.Slug, t))], result.Warnings);
    }

    private static McpTestToolDto Summary(string slug, McpDiscoveredTool tool)
    {
        var definition = JsonNode.Parse(tool.Definition) as JsonObject;
        var description = definition?["description"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        return new McpTestToolDto(tool.Name, McpToolSync.Title(tool.Definition), description, McpToolNames.Exposed(slug, tool.Name));
    }

    // ---- tools -------------------------------------------------------------------------------------------

    public async Task<List<McpToolDto>> ToolsAsync(ClaimsPrincipal user, Guid serverId, CancellationToken ct)
    {
        _ = await FindAsync(user, serverId, ct);
        return [.. (await ctx.Db.McpTools.AsNoTracking().Where(t => t.McpServerId == serverId).OrderBy(t => t.Name).ToListAsync(ct)).Select(McpToolDto.Of)];
    }

    /// <summary>
    /// Approves the tool's current definition, if it is still the one the admin reviewed (<see cref="ApproveToolRequest.DefinitionHash"/>):
    /// clients see it after the gateways reload.
    /// </summary>
    public async Task<McpToolDto> ApproveAsync(ClaimsPrincipal user, Guid serverId, Guid toolId, ApproveToolRequest input, CancellationToken ct)
    {
        var (server, tool) = await ToolAsync(user, serverId, toolId, ct);
        if (tool.DefinitionHash != input.DefinitionHash)
        {
            throw new ApiFaultException(409, "Verktygets definition har ändrats sedan den granskades. Granska den nya definitionen och godkänn igen.");
        }

        var exposedName = McpToolNames.Exposed(server.Slug, tool.Name);
        if (await ctx.Db.McpTools.AnyAsync(t => t.Id != tool.Id && t.ExposedName == exposedName, ct))
        {
            throw new ApiFaultException(409, $"Ett annat verktyg har redan namnet {exposedName} på /mcp.");
        }

        var before = McpToolAudit.Of(tool);
        tool.ExposedName = exposedName;
        McpToolSync.Approve(tool, AdminContext.Actor(user), ctx.Now);
        await ctx.SaveAsync(user, AuditActions.Approve, AuditEntities.McpTool, tool.Id, before, McpToolAudit.Of(tool), InvalidationKind.Config, ct);
        return McpToolDto.Of(tool);
    }

    /// <summary>Hides the tool from every key until it is approved again; a changed tool's earlier approval is dropped too.</summary>
    public async Task<McpToolDto> RejectAsync(ClaimsPrincipal user, Guid serverId, Guid toolId, CancellationToken ct)
    {
        var (_, tool) = await ToolAsync(user, serverId, toolId, ct);
        var before = McpToolAudit.Of(tool);
        tool.ApprovalState = McpToolApprovalState.Rejected;
        tool.ApprovedDefinition = null;
        tool.ApprovedAt = null;
        tool.ApprovedBy = null;
        await ctx.SaveAsync(user, AuditActions.Reject, AuditEntities.McpTool, tool.Id, before, McpToolAudit.Of(tool), InvalidationKind.Config, ct);
        return McpToolDto.Of(tool);
    }

    public async Task<McpToolDto> SetPriceAsync(ClaimsPrincipal user, Guid serverId, Guid toolId, decimal? priceSek, CancellationToken ct)
    {
        var (_, tool) = await ToolAsync(user, serverId, toolId, ct);
        var before = McpToolAudit.Of(tool);
        tool.PriceSek = priceSek;
        await ctx.SaveAsync(user, AuditActions.Price, AuditEntities.McpTool, tool.Id, before, McpToolAudit.Of(tool), InvalidationKind.Config, ct);
        return McpToolDto.Of(tool);
    }

    private async Task<(McpServer Server, McpTool Tool)> ToolAsync(ClaimsPrincipal user, Guid serverId, Guid toolId, CancellationToken ct)
    {
        var server = await ManageableAsync(user, serverId, ct);
        var tool = await ctx.Db.McpTools.SingleOrDefaultAsync(t => t.Id == toolId && t.McpServerId == serverId, ct)
            ?? throw new ApiFaultException(404, "Verktyget finns inte.");
        return (server, tool);
    }

    // ---- checks ------------------------------------------------------------------------------------------

    /// <summary>A department admin's server belongs to one of the admin's departments; only gateway admins create global servers.</summary>
    private async Task EnsureOwnerAsync(ClaimsPrincipal user, Guid? department, CancellationToken ct)
    {
        if (department is not { } id)
        {
            if (!AdminContext.IsAdmin(user))
            {
                throw new ApiFaultException(403, ManageDenied);
            }

            return;
        }

        if (!await ctx.Departments(user).AnyAsync(d => d.Id == id, ct))
        {
            throw Invalid("ownerDepartmentId", "Förvaltningen finns inte.");
        }
    }

    private async Task EnsureSlugFreeAsync(string slug, Guid? id, CancellationToken ct)
    {
        if (await ctx.Db.McpServers.AnyAsync(s => s.Slug == slug && s.Id != id, ct))
        {
            throw new ApiFaultException(409, $"Det finns redan en MCP-server med kortnamnet {slug}.");
        }
    }

    /// <summary>
    /// HTTPS in production (the gateway's rule) and the SSRF rules (<see cref="McpUrlGuard"/>) for the server URL and a new
    /// OAuth token URL. Private addresses need a gateway admin, unless the server already had that URL.
    /// </summary>
    public async Task CheckAddressesAsync(ClaimsPrincipal user, McpServerRequest input, McpServer? stored, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var addresses = new List<(string Field, string Address, bool Unchanged)> { ("url", input.Url, stored?.Url == input.Url) };
        if (input.AuthType == McpAuthType.OAuthClientCredentials && input.Credential?.TokenUrl is { } tokenUrl)
        {
            addresses.Add(("credential", tokenUrl, false));
        }

        foreach (var (field, address, unchanged) in addresses)
        {
            var uri = new Uri(address);
            if (environment.IsProduction() && uri.Scheme != Uri.UriSchemeHttps)
            {
                errors[field] = ["I produktion måste adressen använda HTTPS."];
            }
            else if (await guard.CheckAsync(uri, AdminContext.IsAdmin(user) || unchanged, ct) is { } problem)
            {
                errors[field] = [problem];
            }
        }

        if (errors.Count > 0)
        {
            throw ApiFaultException.Validation(errors);
        }
    }

    private static ApiFaultException Invalid(string field, string message) =>
        ApiFaultException.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });

    private async Task<List<McpServerDto>> DtosAsync(ClaimsPrincipal user, IReadOnlyList<McpServer> servers, CancellationToken ct)
    {
        var ids = servers.Select(s => s.Id).ToList();
        var counts = await ctx.Db.McpTools.AsNoTracking().Where(t => ids.Contains(t.McpServerId))
            .GroupBy(t => new { t.McpServerId, t.ApprovalState }).Select(g => new { g.Key.McpServerId, g.Key.ApprovalState, Count = g.Count() })
            .ToListAsync(ct);
        var owners = servers.Where(s => s.OwnerDepartmentId is not null).Select(s => s.OwnerDepartmentId!.Value).Distinct().ToList();
        var departments = await ctx.Db.Departments.AsNoTracking().Where(d => owners.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var admin = AdminContext.IsAdmin(user);
        int Count(Guid server, McpToolApprovalState? state) =>
            counts.Where(c => c.McpServerId == server && (state is null || c.ApprovalState == state)).Sum(c => c.Count);
        return [.. servers.Select(s => new McpServerDto(
            s.Id, s.Slug, s.DisplayName, s.Description, s.Url, s.Residency, s.IsEnabled, s.OwnerDepartmentId,
            s.OwnerDepartmentId is { } owner ? departments.GetValueOrDefault(owner) : null, s.AuthType, !string.IsNullOrEmpty(s.EncryptedCredential),
            s.ForwardHeaders, s.TimeoutSeconds, s.MaxResultBytes, s.AutoApproveNewTools, s.Status, s.LastCheckedAt, s.LastError,
            McpProbe.Flags(s.Capabilities), s.Instructions, s.CreatedAt, Count(s.Id, null), Count(s.Id, McpToolApprovalState.Pending),
            Count(s.Id, McpToolApprovalState.Changed), admin || s.OwnerDepartmentId is not null))];
    }
}
