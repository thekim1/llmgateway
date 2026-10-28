using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi;

/// <summary>
/// The daily models.dev update. <c>Enabled</c>: <c>Admin:AutoUpdatePrices</c> is on and a list is set. <c>LastRunAt</c>,
/// <c>Followed</c>, <c>Updated</c>, <c>Enriched</c> and <c>Error</c> describe this admin API instance's last run (null before the first).
/// </summary>
public sealed record ModelListSyncDto(
    bool Enabled, string? PriceListUrl, DateTimeOffset? LastRunAt, int Followed, int Updated, int Enriched, string? Error,
    DateTimeOffset? NextRunAt, bool Running);

/// <summary>
/// Once a day, adds the models.dev list price to the price history of every model that follows it (<see cref="PriceMode.List"/>)
/// when it changed, gives an Azure deployment without any price the list price, and fills an empty description, capability
/// tags and context window of any listed model. A new price
/// applies from the time of the check; earlier usage keeps its price. Several admin API instances may run it at the same
/// time: a database advisory lock lets one of them do it.
/// </summary>
public sealed partial class ModelListSync(IServiceScopeFactory scopes, ModelListService list, TimeProvider time, ILogger<ModelListSync> logger)
    : BackgroundService
{
    /// <summary>Written to the audit log as the one who changed the price or details.</summary>
    public const string Actor = "system:price-list";

    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);

    // Any constant shared by every instance; the key of the transaction-scoped advisory lock.
    private const long LockKey = 0x756d_6570_7269_6365; // "umeprice"

    private readonly SemaphoreSlim _trigger = new(0, 1);
    private int _running;
    private DateTimeOffset? _lastRunAt;
    private DateTimeOffset? _nextRunAt;
    private RunResult _result = new(0, 0, 0);
    private string? _error;

    private sealed record RunResult(int Followed, int Updated, int Enriched);

    public bool Enabled => list.UpdatesPrices;

    public ModelListSyncDto Status() => new(
        Enabled, list.Enabled ? list.Url : null, _lastRunAt, _result.Followed, _result.Updated, _result.Enriched, _error,
        Enabled ? _nextRunAt : null, Volatile.Read(ref _running) == 1);

    /// <summary>Starts a run now unless one is running or already requested.</summary>
    public bool TryTrigger()
    {
        if (!Enabled || Volatile.Read(ref _running) == 1)
        {
            return false;
        }

        try
        {
            _trigger.Release();
            return true;
        }
        catch (SemaphoreFullException)
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        var wait = FirstRunDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            _nextRunAt = time.GetUtcNow() + wait;
            try
            {
                await _trigger.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnceAsync(stoppingToken);
            wait = Interval;
        }
    }

    /// <summary>One run with a freshly downloaded list; never throws except on shutdown. Returns the prices added.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return 0;
        }

        try
        {
            var models = await list.GetAsync(cancellationToken, reload: true);
            if (list.LastError is { } listError)
            {
                Finish(new RunResult(0, 0, 0), listError);
                return 0;
            }

            await using var scope = scopes.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<AdminContext>();
            try
            {
                var result = await ApplyAsync(ctx, models, cancellationToken);
                if (result.Updated + result.Enriched > 0)
                {
                    await ctx.PublishAsync([InvalidationKind.Config], cancellationToken);
                }
                LogRun(logger, result.Followed, result.Updated, result.Enriched);
                Finish(result, null);
                return result.Updated;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Shown in the admin UI: the type and SQL state only, never a message that could hold connection details.
                LogRunFailed(logger, ex);
                Finish(new RunResult(0, 0, 0), ex is DbException { SqlState: { } state } ? $"{ex.GetType().Name} (SQL state {state})" : ex.GetType().Name);
                return 0;
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private void Finish(RunResult result, string? error)
    {
        _lastRunAt = time.GetUtcNow();
        _result = result;
        _error = error;
    }

    /// <summary>Adds the changed list prices and fills empty details, each with an audit entry, in one transaction.</summary>
    private static async Task<RunResult> ApplyAsync(AdminContext ctx, ModelList models, CancellationToken ct) =>
        await ctx.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            ctx.Db.ChangeTracker.Clear();
            await using var transaction = await ctx.Db.Database.BeginTransactionAsync(ct);
            if (!await ctx.Db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock({LockKey}) AS "Value" """).SingleAsync(ct))
            {
                return new RunResult(0, 0, 0); // another instance is updating right now
            }

            var now = ctx.Now;
            var deployments = await ctx.Db.ModelDeployments.Include(m => m.ProviderAccount).Include(m => m.Prices).OrderBy(m => m.Name).ToListAsync(ct);
            int followed = 0, updated = 0, enriched = 0;
            foreach (var m in deployments)
            {
                if (models.Find(m) is not { } listed)
                {
                    continue;
                }

                var before = ModelAudit.Of(m);
                if (ModelList.Enrich(m, listed))
                {
                    Audit(ctx, now, AuditActions.Update, m.Id, before, ModelAudit.Of(m));
                    enriched++;
                }

                if (models.FillsMissingPrice(m, updatesOn: true))
                {
                    AddPrice(ctx, now, m, null, ModelList.Changed(null, listed.Price!)!, listed.Key);
                    updated++;
                    continue;
                }

                if (models.Mode(m, updatesOn: true) != PriceMode.List)
                {
                    continue;
                }

                followed++;
                // A price set for the future stands until then; the list is checked again after it.
                if (m.Prices.Any(p => p.EffectiveFrom > now))
                {
                    continue;
                }

                var current = m.PriceAt(now);
                if (ModelList.Changed(current, listed.Price!) is not { } next)
                {
                    continue;
                }

                AddPrice(ctx, now, m, current, next, listed.Key);
                updated++;
            }

            await ctx.Db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new RunResult(followed, updated, enriched);
        });

    private static void AddPrice(AdminContext ctx, DateTimeOffset now, ModelDeployment m, ModelPrice? current, ModelPrice next, string key)
    {
        next.ModelDeploymentId = m.Id;
        next.EffectiveFrom = now;
        next.Source = $"models.dev {key}";
        ctx.Db.ModelPrices.Add(next);
        Audit(ctx, now, AuditActions.Price, m.Id, current is null ? null : PriceDto.Of(current), PriceDto.Of(next));
    }

    private static void Audit(AdminContext ctx, DateTimeOffset now, string action, Guid model, object? before, object after) =>
        ctx.Db.AuditLog.Add(new AuditLogEntry
        {
            Timestamp = now, Actor = Actor, Action = action, EntityType = AuditEntities.Model, EntityId = model.ToString(),
            Details = JsonSerializer.Serialize(new AuditDetails(before, after), AuditJson.Options),
        });

    public override void Dispose()
    {
        _trigger.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Model list update: {Followed} models follow the list price, {Updated} prices updated, {Enriched} models given details")]
    private static partial void LogRun(ILogger logger, int followed, int updated, int enriched);

    [LoggerMessage(Level = LogLevel.Error, Message = "Model list update failed; prices and details are unchanged")]
    private static partial void LogRunFailed(ILogger logger, Exception ex);
}
