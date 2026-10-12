using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi;

/// <summary>Bound from the <c>Retention</c> configuration section. How long data is kept is set in the admin UI (Settings).</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Run the nightly removal. Off, nothing is removed except by <c>Run now</c> in the admin UI.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local time (Europe/Stockholm) of the nightly run.</summary>
    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string RunAt { get; set; } = "03:00";

    /// <summary>Rows removed per database call; each call is its own short transaction.</summary>
    [Range(100, 50_000)]
    public int BatchSize { get; set; } = 5000;
}

/// <summary>
/// Removes usage, refused-credential, alert and audit rows older than the retention set in the admin UI, every night and on
/// demand, through the database function <c>ume_retention_step</c> (it saves monthly totals before usage is removed). Several
/// admin API instances may run it at the same time: the function serialises them on the settings row.
/// </summary>
public sealed partial class RetentionService(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider time, ILogger<RetentionService> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public bool Enabled => options.Value.Enabled;

    /// <summary>The next scheduled run after <paramref name="now"/>, or null when the nightly run is off.</summary>
    public DateTimeOffset? NextRunAt(DateTimeOffset now)
    {
        if (!Enabled)
        {
            return null;
        }

        var runAt = TimeOnly.ParseExact(options.Value.RunAt, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var local = TimeZoneInfo.ConvertTime(now, BudgetPeriods.Stockholm);
        for (var day = 0; day <= 2; day++)
        {
            var candidate = DateOnly.FromDateTime(local.DateTime).AddDays(day).ToDateTime(runAt);
            var at = new DateTimeOffset(candidate, BudgetPeriods.Stockholm.GetUtcOffset(candidate));
            if (at > now)
            {
                return at.ToUniversalTime();
            }
        }

        return now.AddDays(1);
    }

    /// <summary>Starts a run now unless one is running or already requested.</summary>
    public bool TryTrigger()
    {
        if (IsRunning)
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
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var wait = NextRunAt(now) is { } next ? next - now : Timeout.InfiniteTimeSpan;
            try
            {
                await _trigger.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>One complete run; never throws except on shutdown. Returns the rows removed.</summary>
    public async Task<long> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return 0;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var batch = options.Value.BatchSize;
            long removed = 0;
            string? error = null;
            try
            {
                long step;
                do
                {
                    step = await db.Database.SqlQuery<long>($"""SELECT ume_retention_step({batch}) AS "Value" """).SingleAsync(cancellationToken);
                    removed += step;
                }
                while (step > 0);
                LogRun(logger, removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Shown in the admin UI: the type and SQL state only, never a message that could hold connection details.
                error = ex is DbException { SqlState: { } state } ? $"{ex.GetType().Name} (SQL state {state})" : ex.GetType().Name;
                LogRunFailed(logger, ex, removed);
            }

            await db.Settings.Where(s => s.Id == GatewaySettings.SingletonId).ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RetentionLastRunAt, time.GetUtcNow())
                .SetProperty(s => s.RetentionLastRunDeleted, removed)
                .SetProperty(s => s.RetentionLastRunError, error), cancellationToken);
            return removed;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public override void Dispose()
    {
        _trigger.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Data retention run removed {Removed} rows")]
    private static partial void LogRun(ILogger logger, long removed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Data retention run failed after removing {Removed} rows; the next run continues")]
    private static partial void LogRunFailed(ILogger logger, Exception ex, long removed);
}
