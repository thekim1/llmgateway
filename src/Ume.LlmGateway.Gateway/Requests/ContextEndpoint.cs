using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// <c>GET /v1/ume/context</c> (docs/integrations.md): what the presented key is and what it may use, so another system
/// can fill its settings from the gateway instead of keeping copies. For a credential key of an integration: the
/// integration, its assigned keys with their settings, and their teams and departments (every department and team,
/// with the departments' member groups, when the key holds the scope <c>organisation.read</c>); for any other key: the key, its team and department.
/// Answered from the key cache (a credential key carries its assigned keys), with an <c>ETag</c>. Not accounted, like
/// <c>/v1/models</c>.
/// </summary>
public sealed class ContextEndpoint(RequestStages stages, IOptionsMonitor<GatewayOptions> options, TimeProvider time)
{
    /// <summary>What this gateway supports, for clients to test instead of comparing versions. Values are only added.</summary>
    public static IReadOnlyList<string> Features { get; } = ["max-residency", "model-facts", "integrations", "act-as-key", "run-metadata", "stream-cost", "credential-residencies", "organisation", "member-groups"];

    /// <summary>The release version, without the commit after <c>+</c> (as <c>/version</c> reports it in full).</summary>
    private static readonly string? Version = typeof(ContextEndpoint).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];

    public async Task GetAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var ct = http.RequestAborted;
        var now = time.GetUtcNow();
        if (await stages.AuthenticateAsync(http, GatewayEndpoint.Context, now, ct) is not { } authenticated)
        {
            return;
        }

        // With x-ume-on-behalf-of, the context of the assigned key: as if it had been presented.
        await CacheableJson.WriteAsync(http, Build(authenticated.Key, now, options.CurrentValue.AdminUrl), ct);
    }

    internal static JsonObject Build(VirtualKey key, DateTimeOffset now, string? adminUrl)
    {
        var credential = key is { IntegrationRole: IntegrationRole.Credential, Integration: not null };
        IReadOnlyList<VirtualKey> assigned = credential ? key.AssignedKeys : [];

        // The teams the answer is about: the key's own, and those of the keys assigned to its integration.
        var teams = new Dictionary<Guid, Team>();
        if (key.Team is { } own)
        {
            teams[own.Id] = own;
        }

        foreach (var k in assigned)
        {
            if (k.Team is { } team)
            {
                teams.TryAdd(team.Id, team);
            }
        }

        IEnumerable<Department> departments = teams.Values.Select(t => t.Department).OfType<Department>().DistinctBy(d => d.Id);
        IEnumerable<Team> listedTeams = teams.Values;
        var wholeOrganisation = false;
        if (credential && key.Organisation is { } organisation)
        {
            // The scope organisation.read: the whole organisation, which includes the teams above.
            departments = organisation;
            listedTeams = organisation.SelectMany(d => d.Teams);
            wholeOrganisation = true;
        }

        return new JsonObject
        {
            ["object"] = "ume.context",
            ["gateway"] = new JsonObject
            {
                ["version"] = Version,
                ["admin_url"] = string.IsNullOrEmpty(adminUrl) ? null : adminUrl,
                ["features"] = new JsonArray([.. Features.Select(f => (JsonNode?)JsonValue.Create(f))]),
            },
            ["key"] = new JsonObject
            {
                ["id"] = key.Id,
                ["name"] = key.Name,
                ["prefix"] = key.Prefix,
                ["team_id"] = key.TeamId,
                ["integration_role"] = key.IntegrationRole.ToString(),
                ["integration_scopes"] = Strings(key.IntegrationScopes),
                ["require_max_residency"] = key.RequireMaxResidency,
                ["allowed_residencies"] = Strings(key.OwnAllowedResidencies.Select(r => r.ToString())),
            },
            ["integration"] = credential
                ? new JsonObject { ["id"] = key.Integration!.Id, ["name"] = key.Integration.Name, ["owner_team_id"] = key.Integration.OwnerTeamId }
                : null,
            ["departments"] = new JsonArray([.. departments.OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id).Select(d => (JsonNode?)Department(d, wholeOrganisation))]),
            ["teams"] = new JsonArray([.. listedTeams.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id).Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id,
                ["department_id"] = t.DepartmentId,
                ["name"] = t.Name,
                ["code"] = t.Code,
                ["is_active"] = t.IsActive,
            })]),
            ["assigned_keys"] = new JsonArray([.. assigned.Select(k => (JsonNode?)AssignedKey(k, now))]),
        };
    }

    /// <summary>Member groups (who belongs to the department at sign-in) only with the scope <c>organisation.read</c>.</summary>
    private static JsonObject Department(Department d, bool withMemberGroups)
    {
        var department = new JsonObject
        {
            ["id"] = d.Id,
            ["name"] = d.Name,
            ["cost_center_code"] = d.CostCenterCode,
            ["is_active"] = d.IsActive,
        };
        if (withMemberGroups)
        {
            department["member_groups"] = Strings(d.MemberGroups);
        }

        return department;
    }

    /// <summary>
    /// What a platform shows about an assigned key and checks before using it: never the secret or its hash. The output cap
    /// and system prompt are the key's and its team's request policy combined, as a request gets them.
    /// </summary>
    private static JsonObject AssignedKey(VirtualKey k, DateTimeOffset now)
    {
        var policy = Domain.Requests.RequestPolicy.Merge(k.Team?.RequestPolicy, k.RequestPolicy);
        return new JsonObject
        {
            ["id"] = k.Id,
            ["name"] = k.Name,
            ["description"] = k.Description,
            ["team_id"] = k.TeamId,
            ["department_id"] = k.Team?.DepartmentId,
            ["status"] = k.GetStatus(now).ToString(),
            ["owner_active"] = k.Team is { IsActive: true, Department.IsActive: true },
            ["expires_at"] = k.ExpiresAt,
            ["allowed_models"] = Strings(k.AllowedModels),
            ["allowed_residencies"] = Strings(k.OwnAllowedResidencies.Select(r => r.ToString())),
            ["usable"] = !k.NoCommonResidency,
            ["allowed_providers"] = Strings(k.AllowedProviders),
            ["pii_policy"] = k.PiiPolicy.ToString(),
            ["content_guard_policy"] = k.ContentGuardPolicy.ToString(),
            ["attachment_policy"] = k.AttachmentPolicy.ToString(),
            ["allow_provider_tools"] = k.AllowProviderTools,
            ["allow_provider_storage"] = k.AllowProviderStorage,
            ["requests_per_minute"] = k.RequestsPerMinute,
            ["tokens_per_minute"] = k.TokensPerMinute,
            ["max_concurrent_requests"] = k.MaxConcurrentRequests,
            ["max_output_tokens"] = policy?.MaxOutputTokens,
            ["has_system_prompt"] = !string.IsNullOrEmpty(policy?.SystemPrompt),
        };
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
}
