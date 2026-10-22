using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed partial class AdminContext
{
    /// <summary>Set on a response whose change is saved but could not be announced; gateways pick it up when their cache expires.</summary>
    public const string PropagationHeader = "Ume-Change-Propagation";

    private static readonly TimeSpan PublishRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly IInvalidationBus _bus;
    private readonly TimeProvider _time;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger _security;
    private readonly ILogger _logger;
    private List<AuditLogEntry> _saving = [];

    public AdminContext(GatewayDbContext db, IInvalidationBus bus, TimeProvider time, ILoggerFactory loggers, IHttpContextAccessor http)
    {
        Db = db;
        _bus = bus;
        _time = time;
        _http = http;
        _security = SecurityEvents.CreateLogger(loggers);
        _logger = loggers.CreateLogger<AdminContext>();

        // Every audit entry, however it was added (including config import), says who made it, from where and in which
        // session, and becomes an admin.change security event once it is saved. Details are not logged: they stay in the
        // audit log, where secrets are masked.
        db.SavingChanges += (_, _) =>
        {
            _saving = [.. db.ChangeTracker.Entries<AuditLogEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity)];
            if (_saving.Count > 0 && _http.HttpContext is { } http)
            {
                var actor = AdminActor.Of(http);
                foreach (var entry in _saving.Where(e => e.RequestId is null))
                {
                    actor.Stamp(entry);
                }
            }
        };
        db.SavedChanges += (_, _) =>
        {
            foreach (var entry in _saving)
            {
                SecurityEvents.AdminChange(_security, entry.Actor, entry.Action, entry.EntityType, entry.EntityId);
            }

            _saving = [];
        };
        db.SaveChangesFailed += (_, _) => _saving = [];
    }

    public GatewayDbContext Db { get; }
    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The signed-in user of the current request. Every scoped query and audit entry below is for this user.</summary>
    public ClaimsPrincipal User => _http.HttpContext?.User
        ?? throw new InvalidOperationException("AdminContext is only available while handling an HTTP request.");

    public bool IsAdmin => User.IsInRole(AdminRoles.GatewayAdmin);
    private string[] DepartmentCodes => [.. User.FindAll(AdminClaims.DepartmentCodes).Select(c => c.Value)];

    /// <summary>The user's OIDC subject, or null when the identity has none (recorded as a key's creator).</summary>
    public string? Subject => User.FindFirstValue(AdminClaims.Subject);

    /// <summary>Who made a change, as written to the audit log: the OIDC subject, else the name.</summary>
    public string Actor => Subject ?? User.Identity?.Name ?? "unknown";

    public IQueryable<Department> Departments()
    {
        if (IsAdmin)
        {
            return Db.Departments;
        }

        var codes = DepartmentCodes;
        return Db.Departments.Where(d => codes.Contains(d.CostCenterCode));
    }

    public IQueryable<Team> Teams() =>
        Db.Teams.Include(t => t.Department).Where(t => Departments().Any(d => d.Id == t.DepartmentId));
    public IQueryable<VirtualKey> Keys() =>
        Db.VirtualKeys.Include(k => k.Team!).ThenInclude(t => t.Department).Where(k => Teams().Any(t => t.Id == k.TeamId));
    public IQueryable<UsageRecord> Usage() =>
        Db.UsageRecords.Where(u => Departments().Any(d => d.Id == u.DepartmentId));
    public IQueryable<UsageMonthlyTotal> UsageTotals() =>
        Db.UsageMonthlyTotals.AsNoTracking().Where(t => Departments().Any(d => d.Id == t.DepartmentId));

    /// <summary>
    /// The audit entry for a change. <paramref name="before"/> and <paramref name="after"/> are snapshot records (see AuditSnapshots.cs)
    /// that never hold secrets.
    /// </summary>
    public AuditLogEntry Audit(string action, string entityType, object? id, object? before, object? after) => new()
    {
        Timestamp = Now, Actor = Actor, Action = action, EntityType = entityType, EntityId = id?.ToString(),
        Details = JsonSerializer.Serialize(new AuditDetails(before, after), AuditJson.Options),
    };

    /// <summary>Writes an audit entry without publishing a cache invalidation (for read-only actions such as revealing a key).</summary>
    public async Task AuditAsync(string action, string entityType, object id, object? details, CancellationToken ct)
    {
        StageAudit(action, entityType, id, null, details);
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>Adds an audit entry to the current unit of work without saving; the next <see cref="SaveAsync(string, string, object, object?, object?, InvalidationKind, CancellationToken)"/> commits it together with the change.</summary>
    public void StageAudit(string action, string entityType, object id, object? before, object? after) =>
        Db.AuditLog.Add(Audit(action, entityType, id, before, after));

    /// <summary>Saves the change and its audit entry in one transaction, then tells the gateways to reload.</summary>
    public Task SaveAsync(string action, string entityType, object id, object? before, object? after, InvalidationKind kind, CancellationToken ct) =>
        SaveAsync(action, entityType, id, before, after, [kind], ct);

    /// <summary>As above, for a change that affects more than one gateway cache (e.g. a rotated key changes keys and budgets).</summary>
    public async Task SaveAsync(string action, string entityType, object id, object? before, object? after,
        IReadOnlyCollection<InvalidationKind> kinds, CancellationToken ct)
    {
        StageAudit(action, entityType, id, before, after);
        await Db.SaveChangesAsync(ct);
        await PublishAsync(kinds, ct);
    }

    /// <summary>
    /// Tells the gateways to reload; for changes saved elsewhere (a committed import) or caches without a database change.
    /// The change is already saved, so a bus that is down (Redis unavailable) does not fail the request: it is tried once more,
    /// then logged, and the response carries <see cref="PropagationHeader"/>. Gateways reload when their cache expires
    /// (<c>Gateway:KeyCacheSeconds</c>, <c>Gateway:CatalogCacheSeconds</c>).
    /// </summary>
    public async Task PublishAsync(IReadOnlyCollection<InvalidationKind> kinds, CancellationToken ct)
    {
        // The gateways subscribe to one kind per message, so each kind is its own message.
        foreach (var kind in kinds.Distinct())
        {
            if (!await TryPublishAsync(kind, ct) && !await RetryPublishAsync(kind, ct))
            {
                LogPublishFailed(_logger, kind);
                if (_http.HttpContext is { } http)
                {
                    http.Response.Headers[PropagationHeader] = "delayed";
                }
            }
        }
    }

    private async Task<bool> RetryPublishAsync(InvalidationKind kind, CancellationToken ct)
    {
        await Task.Delay(PublishRetryDelay, _time, ct);
        return await TryPublishAsync(kind, ct);
    }

    private async Task<bool> TryPublishAsync(InvalidationKind kind, CancellationToken ct)
    {
        try
        {
            await _bus.PublishAsync(kind, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saved change not announced to the gateways ({Kind}); they reload when their cache expires")]
    private static partial void LogPublishFailed(ILogger logger, InvalidationKind kind);
}

public sealed class AdminValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var argument in context.Arguments.OfType<AdminRequest>())
        {
            Validate(argument, "", errors);
        }
        return errors.Count > 0 ? Results.ValidationProblem(errors, title: ApiFaultException.ValidationTitle) : await next(context);
    }

    private static void Validate(AdminRequest argument, string prefix, Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(argument, new ValidationContext(argument), results, validateAllProperties: true);
        foreach (var result in results)
        {
            foreach (var member in result.MemberNames.DefaultIfEmpty("request"))
            {
                var field = prefix + JsonNamingPolicy.CamelCase.ConvertName(member);
                errors[field] = [result.ErrorMessage ?? "The value is invalid."];
            }
        }
        foreach (var property in argument.GetType().GetProperties())
        {
            if (property.GetValue(argument) is AdminRequest nested)
            {
                Validate(nested, prefix + property.Name + ".", errors);
            }
            else if (property.GetValue(argument) is IEnumerable<AdminRequest> children)
            {
                var i = 0;
                foreach (var child in children)
                {
                    Validate(child, $"{prefix}{property.Name}[{i++}].", errors);
                }
            }
        }
    }
}

public abstract record AdminRequest;
