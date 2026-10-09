using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>
/// Counts refused credentials per (reason, endpoint, key, source address) and periodically writes one
/// <see cref="AuthFailure"/> row and one <c>gateway.auth.failed</c> security event per bucket. Unauthenticated callers
/// can send any number of requests, so the number of buckets per interval is capped; the rest is folded into an
/// overflow bucket per reason and endpoint. The presented key itself is never kept.
/// </summary>
public sealed partial class AuthFailureRecorder(
    IServiceScopeFactory scopes,
    IOptionsMonitor<GatewayOptions> options,
    TimeProvider time,
    ILoggerFactory loggers,
    ILogger<AuthFailureRecorder> logger) : BackgroundService
{
    private readonly record struct Bucket(AuthFailureReason Reason, GatewayEndpoint Endpoint, Guid? KeyId, string? SourceAddress);

    private readonly Lock _gate = new();
    // Not disposed on purpose: the container may dispose this singleton before the host's final StopAsync flush,
    // and a SemaphoreSlim whose wait handle is never used holds nothing that needs releasing.
#pragma warning disable CA2213
    private readonly SemaphoreSlim _flushing = new(1, 1);
#pragma warning restore CA2213
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);
    private Dictionary<Bucket, AuthFailure> _buckets = [];

    /// <summary>Rows whose database write failed; retried on the next flush (bounded like the buckets).</summary>
    private List<AuthFailure> _unwritten = [];

    public void Record(AuthFailureReason reason, GatewayEndpoint endpoint, VirtualKey? key, IPAddress? address)
    {
        var settings = options.CurrentValue.Security;
        var now = time.GetUtcNow();
        var bucket = new Bucket(reason, endpoint, key?.Id, FormatAddress(address, settings.SourceAddress));
        lock (_gate)
        {
            if (!_buckets.TryGetValue(bucket, out var row))
            {
                if (_buckets.Count >= settings.MaxAuthFailureBuckets)
                {
                    bucket = new Bucket(reason, endpoint, null, null);
                    key = null;
                }

                if (!_buckets.TryGetValue(bucket, out row))
                {
                    row = new AuthFailure
                    {
                        FirstSeen = now, Reason = reason, Endpoint = endpoint, VirtualKeyId = key?.Id, KeyPrefix = key?.Prefix,
                        TeamId = key?.TeamId, DepartmentId = key?.Team?.DepartmentId, SourceAddress = bucket.SourceAddress,
                    };
                    _buckets.Add(bucket, row);
                }
            }

            row.Count++;
            row.LastSeen = now;
        }
    }

    /// <summary>Writes and logs everything counted so far (called on a timer, at shutdown and by tests).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _flushing.WaitAsync(cancellationToken);
        try
        {
            List<AuthFailure> rows;
            lock (_gate)
            {
                rows = [.. _buckets.Values];
                _buckets = [];
            }

            foreach (var row in rows)
            {
                SecurityEvents.AuthFailed(_security, SecurityEvents.ReasonName(row.Reason), row.Endpoint.ToString(), row.Count, row.SourceAddress,
                    row.VirtualKeyId, row.KeyPrefix, row.TeamId, row.DepartmentId, row.FirstSeen, row.LastSeen);
            }

            rows.AddRange(_unwritten);
            if (rows.Count == 0)
            {
                return;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                db.AuthFailures.AddRange(rows);
                await db.SaveChangesAsync(cancellationToken);
                _unwritten = [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Already logged as security events; keep the newest rows for the database and drop the oldest.
                var max = options.CurrentValue.Security.MaxAuthFailureBuckets;
                _unwritten = [.. rows.OrderByDescending(r => r.LastSeen).Take(max).Select(r => { r.Id = 0; return r; })];
                LogWriteFailed(logger, ex.GetType().Name, rows.Count);
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval(), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
                timer.Period = Interval();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; StopAsync does the final flush.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timed out; the failures were counted but not written.
        }
    }

    private TimeSpan Interval() => TimeSpan.FromSeconds(options.CurrentValue.Security.AuthFailureFlushSeconds);

    /// <summary>Applies <see cref="SourceAddressMode"/>: truncated keeps IPv4 /24 or IPv6 /48.</summary>
    public static string? FormatAddress(IPAddress? address, SourceAddressMode mode)
    {
        if (address is null || mode == SourceAddressMode.None)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (mode == SourceAddressMode.Full)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        bytes.AsSpan(address.AddressFamily == AddressFamily.InterNetwork ? 3 : 6).Clear();
        return new IPAddress(bytes).ToString();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write {Count} authentication failure rows ({ExceptionType}); retrying on the next flush")]
    private static partial void LogWriteFailed(ILogger logger, string exceptionType, int count);
}
