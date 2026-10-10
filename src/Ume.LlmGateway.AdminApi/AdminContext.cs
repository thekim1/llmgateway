using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

public sealed class AdminContext
{
    private readonly IInvalidationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger _security;
    private List<AuditLogEntry> _saving = [];

    public AdminContext(GatewayDbContext db, IInvalidationBus bus, TimeProvider time, ILoggerFactory loggers)
    {
        Db = db;
        _bus = bus;
        _time = time;
        _security = SecurityEvents.CreateLogger(loggers);

        // Every audit entry, however it was added (including config import), becomes an admin.change security
        // event once it is saved. Details are not logged: they stay in the audit log, where secrets are masked.
        db.SavingChanges += (_, _) => _saving = [.. db.ChangeTracker.Entries<AuditLogEntry>().Where(e => e.State == EntityState.Added).Select(e => e.Entity)];
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
    public static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole("gateway-admin");
    public static string[] Codes(ClaimsPrincipal user) => [.. user.FindAll("departmentCodes").Select(c => c.Value)];

    public IQueryable<Department> Departments(ClaimsPrincipal user) =>
        IsAdmin(user) ? Db.Departments : Db.Departments.Where(d => Codes(user).Contains(d.CostCenterCode));
    public IQueryable<Team> Teams(ClaimsPrincipal user) =>
        Db.Teams.Include(t => t.Department).Where(t => Departments(user).Any(d => d.Id == t.DepartmentId));
    public IQueryable<VirtualKey> Keys(ClaimsPrincipal user) =>
        Db.VirtualKeys.Include(k => k.Team!).ThenInclude(t => t.Department).Where(k => Teams(user).Any(t => t.Id == k.TeamId));
    public IQueryable<UsageRecord> Usage(ClaimsPrincipal user) =>
        Db.UsageRecords.Where(u => Departments(user).Any(d => d.Id == u.DepartmentId));

    /// <summary>Who made a change, as written to the audit log: the OIDC subject, else the name.</summary>
    public static string Actor(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? user.Identity?.Name ?? "unknown";

    /// <summary>
    /// The audit entry for a change. <paramref name="before"/> and <paramref name="after"/> are snapshot records (see AuditSnapshots.cs)
    /// that never hold secrets.
    /// </summary>
    public AuditLogEntry Audit(ClaimsPrincipal user, string action, string entityType, object? id, object? before, object? after) => new()
    {
        Timestamp = Now, Actor = Actor(user), Action = action, EntityType = entityType, EntityId = id?.ToString(),
        Details = JsonSerializer.Serialize(new AuditDetails(before, after), AuditJson.Options),
    };

    /// <summary>Writes an audit entry without publishing a cache invalidation (for read-only actions such as revealing a key).</summary>
    public async Task AuditAsync(ClaimsPrincipal user, string action, string entityType, object id, object? details, CancellationToken ct)
    {
        StageAudit(user, action, entityType, id, null, details);
        await Db.SaveChangesAsync(ct);
    }

    /// <summary>Adds an audit entry to the current unit of work without saving; the next <see cref="SaveAsync(ClaimsPrincipal, string, string, object, object?, object?, InvalidationKind, CancellationToken)"/> commits it together with the change.</summary>
    public void StageAudit(ClaimsPrincipal user, string action, string entityType, object id, object? before, object? after) =>
        Db.AuditLog.Add(Audit(user, action, entityType, id, before, after));

    /// <summary>Saves the change and its audit entry in one transaction, then tells the gateways to reload.</summary>
    public Task SaveAsync(ClaimsPrincipal user, string action, string entityType, object id, object? before, object? after, InvalidationKind kind, CancellationToken ct) =>
        SaveAsync(user, action, entityType, id, before, after, [kind], ct);

    /// <summary>As above, for a change that affects more than one gateway cache (e.g. a rotated key changes keys and budgets).</summary>
    public async Task SaveAsync(ClaimsPrincipal user, string action, string entityType, object id, object? before, object? after,
        IReadOnlyCollection<InvalidationKind> kinds, CancellationToken ct)
    {
        StageAudit(user, action, entityType, id, before, after);
        await Db.SaveChangesAsync(ct);
        await PublishAsync(kinds, ct);
    }

    /// <summary>Tells the gateways to reload; for changes saved elsewhere (a committed import) or caches without a database change.</summary>
    public async Task PublishAsync(IReadOnlyCollection<InvalidationKind> kinds, CancellationToken ct)
    {
        // The gateways subscribe to one kind per message, so each kind is its own message.
        foreach (var kind in kinds.Distinct())
        {
            await _bus.PublishAsync(kind, ct);
        }
    }
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
                errors[field] = [result.ErrorMessage ?? "Värdet är ogiltigt."];
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
