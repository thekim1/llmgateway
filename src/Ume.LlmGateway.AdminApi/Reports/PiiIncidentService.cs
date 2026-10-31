using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.AdminApi.Reports;

/// <summary>Filters of <c>GET /api/usage/pii-incidents</c>; <see cref="Category"/> and <see cref="Action"/> as written in the query string.</summary>
public sealed record PiiIncidentQuery(
    DateTimeOffset? From, DateTimeOffset? To, Guid? KeyId, Guid? TeamId, Guid? DepartmentId, string? Category, string? Action, int? Page, int? PageSize);

/// <summary>
/// PII incidents: usage records in which the gateway found PII (<c>PiiCategories</c> is set), in the user's departments.
/// A key with policy <c>Off</c> is never scanned, so it never has incidents.
/// </summary>
public sealed class PiiIncidentService(AdminContext ctx)
{
    public async Task<PiiIncidentReportDto> ListAsync(PiiIncidentQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (skip, take) = ReportQuery.Pagination(query.Page, query.PageSize);
        var (start, end) = ReportQuery.Range(query.From, query.To, ctx.Now);
        PiiCategory? category = query.Category is null ? null
            : EnumQuery.TryParse<PiiCategory>(query.Category, out var parsedCategory) ? parsedCategory
            : throw new ApiFaultException(400, "The PII category is invalid.");
        PiiPolicy? action = query.Action is null ? null
            : EnumQuery.TryParse<PiiPolicy>(query.Action, out var parsedAction) && parsedAction != PiiPolicy.Off ? parsedAction
            : throw new ApiFaultException(400, "The PII action is invalid.");

        // Served by the partial index on Timestamp where PiiCategories is set.
        var incidents = ctx.Usage().AsNoTracking().Where(u => u.PiiCategories != null && u.Timestamp >= start && u.Timestamp < end);
        if (query.KeyId is { } key) { incidents = incidents.Where(u => u.VirtualKeyId == key); }
        if (query.TeamId is { } team) { incidents = incidents.Where(u => u.TeamId == team); }
        if (query.DepartmentId is { } department) { incidents = incidents.Where(u => u.DepartmentId == department); }

        // The summary: one row per distinct category string ("Personnummer:2,Email:1"), split here; there are few of them.
        var combinations = await incidents.GroupBy(u => u.PiiCategories!).Select(g => new { g.Key, Requests = g.LongCount() }).ToListAsync(ct);
        var categories = new Dictionary<PiiCategory, (long Requests, long Occurrences)>();
        foreach (var combination in combinations)
        {
            foreach (var finding in ParseCategories(combination.Key))
            {
                var (requests, occurrences) = categories.GetValueOrDefault(finding.Category);
                categories[finding.Category] = (requests + combination.Requests, occurrences + (finding.Count * combination.Requests));
            }
        }
        var actions = await incidents.Where(u => u.PiiActionApplied != null).GroupBy(u => u.PiiActionApplied!.Value)
            .Select(g => new PiiActionCountDto(g.Key, g.LongCount())).ToListAsync(ct);

        var filtered = incidents;
        if (category is { } c)
        {
            // Category names are distinct and none ends with another, so "Name:" only matches that category.
            var token = c.ToString() + ":";
            filtered = filtered.Where(u => u.PiiCategories!.Contains(token));
        }
        if (action is { } a) { filtered = filtered.Where(u => u.PiiActionApplied == a); }
        var total = await filtered.CountAsync(ct);
        var rows = await UsageQueries.WithNames(filtered.OrderByDescending(u => u.Timestamp).ThenByDescending(u => u.Id).Skip(skip).Take(take), ctx.Db)
            .ToListAsync(ct);

        return new PiiIncidentReportDto(start, end, [.. rows.Select(Incident)], total, combinations.Sum(x => x.Requests),
            [.. categories.OrderBy(x => x.Key).Select(x => new PiiCategoryCountDto(x.Key, x.Value.Requests, x.Value.Occurrences))],
            [.. actions.OrderBy(x => x.Action)]);
    }

    /// <summary>Parses the gateway's summary (<c>Personnummer:2,Email:1</c>, see <c>PiiScanResult.Summary</c>); unknown parts are skipped.</summary>
    internal static List<PiiFindingDto> ParseCategories(string? summary)
    {
        var findings = new List<PiiFindingDto>();
        foreach (var part in (summary ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            var name = colon < 0 ? part : part[..colon];
            var count = 1;
            if (Enum.TryParse<PiiCategory>(name, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
                && (colon < 0 || int.TryParse(part.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out count)))
            {
                findings.Add(new PiiFindingDto(parsed, count));
            }
        }

        return findings;
    }

    private static PiiIncidentDto Incident(UsageRow row)
    {
        var u = row.Record;
        return new PiiIncidentDto(u.RequestId, u.Timestamp, u.VirtualKeyId, row.KeyPrefix, row.KeyName, u.TeamId, row.TeamName, u.DepartmentId,
            row.DepartmentName, GatewayEndpoints.Info(u.Endpoint).ClientPath, u.RequestedModel, u.ProviderName, u.PiiActionApplied ?? PiiPolicy.Allow,
            ParseCategories(u.PiiCategories), u.Outcome, u.StatusCode, u.RoutingRuleName);
    }
}
