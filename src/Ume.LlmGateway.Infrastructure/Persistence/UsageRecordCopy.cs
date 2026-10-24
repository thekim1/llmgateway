using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Infrastructure.Persistence;

/// <summary>
/// Bulk writes for the gateway's usage writer: usage records with Postgres binary <c>COPY</c> (one stream per batch
/// instead of an <c>INSERT … RETURNING</c> per row through the change tracker), and <c>LastUsedAt</c> for every key in a
/// batch in one statement. Both run in the caller's transaction when one is open.
/// </summary>
public static class UsageRecordCopy
{
    /// <summary>
    /// Every column written, in <c>COPY</c> order. <c>Id</c>, <c>RecordedAt</c> and the old <c>CostSek</c> are left to their database defaults.
    /// A test checks this against the EF model, so a new <see cref="UsageRecord"/> column cannot be silently left out.
    /// </summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "RequestId", "Timestamp", "VirtualKeyId", "TeamId", "DepartmentId", "Endpoint", "RequestedModel", "ProviderAccountId",
        "ProviderName", "ModelDeploymentId", "UpstreamModel", "InputTokens", "CachedInputTokens", "OutputTokens", "AudioSeconds",
        "CostUsd", "LatencyMs", "UpstreamMs", "TimeToFirstTokenMs", "StatusCode", "Outcome", "FallbackCount", "Streamed",
        "PiiActionApplied", "PiiCategories", "ErrorCode", "RoutingRuleId", "RoutingRuleName", "GuardFlags",
    ];

    private static readonly string CopyCommand =
        $"""COPY "UsageRecords" ({string.Join(", ", Columns.Select(c => $"\"{c}\""))}) FROM STDIN (FORMAT BINARY)""";

    /// <summary>Writes <paramref name="records"/> with one binary <c>COPY</c>. Ids are not read back.</summary>
    public static async Task CopyUsageRecordsAsync(this GatewayDbContext db, IEnumerable<UsageRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(records);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var copy = await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken);
            foreach (var r in records)
            {
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(r.RequestId, NpgsqlDbType.Varchar, cancellationToken);
                await copy.WriteAsync(r.Timestamp.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await copy.WriteAsync(r.VirtualKeyId, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(r.TeamId, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(r.DepartmentId, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(r.Endpoint.ToString(), NpgsqlDbType.Varchar, cancellationToken);
                await copy.WriteAsync(r.RequestedModel, NpgsqlDbType.Varchar, cancellationToken);
                await WriteAsync(copy, r.ProviderAccountId, NpgsqlDbType.Uuid, cancellationToken);
                await WriteAsync(copy, r.ProviderName, NpgsqlDbType.Varchar, cancellationToken);
                await WriteAsync(copy, r.ModelDeploymentId, NpgsqlDbType.Uuid, cancellationToken);
                await WriteAsync(copy, r.UpstreamModel, NpgsqlDbType.Varchar, cancellationToken);
                await copy.WriteAsync(r.InputTokens, NpgsqlDbType.Bigint, cancellationToken);
                await copy.WriteAsync(r.CachedInputTokens, NpgsqlDbType.Bigint, cancellationToken);
                await copy.WriteAsync(r.OutputTokens, NpgsqlDbType.Bigint, cancellationToken);
                await copy.WriteAsync(r.AudioSeconds, NpgsqlDbType.Numeric, cancellationToken);
                await copy.WriteAsync(r.CostUsd, NpgsqlDbType.Numeric, cancellationToken);
                await copy.WriteAsync(r.LatencyMs, NpgsqlDbType.Integer, cancellationToken);
                await WriteAsync(copy, r.UpstreamMs, NpgsqlDbType.Integer, cancellationToken);
                await WriteAsync(copy, r.TimeToFirstTokenMs, NpgsqlDbType.Integer, cancellationToken);
                await copy.WriteAsync(r.StatusCode, NpgsqlDbType.Integer, cancellationToken);
                await copy.WriteAsync(r.Outcome.ToString(), NpgsqlDbType.Varchar, cancellationToken);
                await copy.WriteAsync(r.FallbackCount, NpgsqlDbType.Integer, cancellationToken);
                await copy.WriteAsync(r.Streamed, NpgsqlDbType.Boolean, cancellationToken);
                await WriteAsync(copy, r.PiiActionApplied?.ToString(), NpgsqlDbType.Varchar, cancellationToken);
                await WriteAsync(copy, r.PiiCategories, NpgsqlDbType.Varchar, cancellationToken);
                await WriteAsync(copy, r.ErrorCode, NpgsqlDbType.Varchar, cancellationToken);
                await WriteAsync(copy, r.RoutingRuleId, NpgsqlDbType.Uuid, cancellationToken);
                await WriteAsync(copy, r.RoutingRuleName, NpgsqlDbType.Varchar, cancellationToken);
                await WriteAsync(copy, r.GuardFlags, NpgsqlDbType.Varchar, cancellationToken);
            }

            await copy.CompleteAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Raises <c>LastUsedAt</c> to the given instant for every key in one statement; a key whose column is already later
    /// is left alone (another instance may have written a newer request).
    /// </summary>
    public static Task<int> UpdateKeysLastUsedAsync(this GatewayDbContext db, IReadOnlyCollection<(Guid KeyId, DateTimeOffset LastUsed)> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return Task.FromResult(0);
        }

        // Sorted, so concurrent writers lock the rows in the same order.
        var sorted = keys.OrderBy(k => k.KeyId).ToArray();
        var ids = sorted.Select(k => k.KeyId).ToArray();
        var lastUsed = sorted.Select(k => k.LastUsed.UtcDateTime).ToArray();
        return db.Database.ExecuteSqlAsync(
            $"""
             UPDATE "VirtualKeys" AS k SET "LastUsedAt" = v.last_used
             FROM unnest({ids}, {lastUsed}) AS v(id, last_used)
             WHERE k."Id" = v.id AND (k."LastUsedAt" IS NULL OR k."LastUsedAt" < v.last_used)
             """, cancellationToken);
    }

    private static Task WriteAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken cancellationToken) where T : class =>
        value is null ? copy.WriteNullAsync(cancellationToken) : copy.WriteAsync(value, type, cancellationToken);

    private static Task WriteAsync<T>(NpgsqlBinaryImporter copy, T? value, NpgsqlDbType type, CancellationToken cancellationToken) where T : struct =>
        value is { } v ? copy.WriteAsync(v, type, cancellationToken) : copy.WriteNullAsync(cancellationToken);
}
