using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class OperationsTests(GatewayFixture fixture)
{
    [Fact]
    public async Task Operations_rejects_unsigned_stale_and_invalid_signatures()
    {
        using var unsigned = await fixture.Client.GetAsync(OperationsSignature.Path, TestContext.Current.CancellationToken);
        unsigned.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        foreach (var offset in new[] { -31, 31 })
        {
            using var request = Signed(DateTimeOffset.UtcNow.AddSeconds(offset).ToUnixTimeSeconds());
            using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        using var invalid = Signed(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "incorrect");
        using var rejected = await fixture.Client.SendAsync(invalid, TestContext.Current.CancellationToken);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Operations_reports_actual_version_and_live_writer_snapshot()
    {
        var writer = fixture.Services.GetRequiredService<UsageWriter>();
        await writer.FlushAsync(TestContext.Current.CancellationToken);
        using var request = Signed(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<GatewayOperationsStatus>(TestContext.Current.CancellationToken);
        status.ShouldNotBeNull().Version.ShouldBe(typeof(Program).Assembly.GetName().Version!.ToString());
        status.Status.ShouldBe("Healthy");
        status.UsageWriter.Capacity.ShouldBe(10_000);
        status.UsageWriter.QueueDepth.ShouldBeGreaterThanOrEqualTo(0);
        status.UsageWriter.ConsecutiveFailures.ShouldBe(0);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Retried_usage_is_idempotent_and_writer_reports_last_success()
    {
        var key = await fixture.CreateKeyAsync();
        var record = new UsageRecord
        {
            RequestId = Guid.NewGuid().ToString("N"), VirtualKeyId = key.Id,
            DepartmentId = fixture.DepartmentId, TeamId = key.TeamId, Timestamp = DateTimeOffset.UtcNow,
            RequestedModel = "test", StatusCode = 200,
        };
        // A commit whose answer was lost: the record is stored, but the writer saw the attempt fail and retries it.
        using (var stored = fixture.Services.CreateScope())
        {
            await stored.ServiceProvider.GetRequiredService<GatewayDbContext>().CopyUsageRecordsAsync([record], TestContext.Current.CancellationToken);
        }

        var fail = 1;
        var scopes = new OutageScopeFactory(fixture.Services, () => Volatile.Read(ref fail) == 1);
        using var writer = new UsageWriter(scopes, fixture.Services.GetRequiredService<AlertNotifier>(), TimeProvider.System,
            NullLogger<UsageWriter>.Instance, fixture.Services.GetRequiredService<GatewayMetrics>());
        await writer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await writer.EnqueueAsync(new UsageWork(record, []), TestContext.Current.CancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (writer.Status.ConsecutiveFailures == 0) { await Task.Delay(10, deadline.Token); }
            Volatile.Write(ref fail, 0);
            await writer.FlushAsync(deadline.Token);
            writer.Status.LastWriteAt.ShouldNotBeNull();
            writer.Status.ConsecutiveFailures.ShouldBe(0);
            using var scope = fixture.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.CountAsync(r => r.RequestId == record.RequestId,
                deadline.Token)).ShouldBe(1);
        }
        finally { await writer.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Writer_retains_failed_batch_and_reports_recovery()
    {
        var key = await fixture.CreateKeyAsync();
        var record = new UsageRecord
        {
            RequestId = Guid.NewGuid().ToString("N"), VirtualKeyId = key.Id, TeamId = key.TeamId,
            DepartmentId = fixture.DepartmentId, Timestamp = DateTimeOffset.UtcNow, RequestedModel = "test", StatusCode = 200,
        };
        var fail = 1;
        var scopes = new OutageScopeFactory(fixture.Services, () => Volatile.Read(ref fail) == 1);
        using var writer = new UsageWriter(scopes, fixture.Services.GetRequiredService<AlertNotifier>(), TimeProvider.System,
            NullLogger<UsageWriter>.Instance, fixture.Services.GetRequiredService<GatewayMetrics>());
        await writer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await writer.EnqueueAsync(new UsageWork(record, []), TestContext.Current.CancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (writer.Status.ConsecutiveFailures == 0) { await Task.Delay(10, deadline.Token); }
            writer.Status.InFlightRecords.ShouldBe(1);
            Volatile.Write(ref fail, 0);
            await writer.FlushAsync(deadline.Token);
            writer.Status.ConsecutiveFailures.ShouldBe(0);
            writer.Status.LastWriteAt.ShouldNotBeNull();
            using var scope = fixture.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.CountAsync(r => r.RequestId == record.RequestId,
                deadline.Token)).ShouldBe(1);
        }
        finally { await writer.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Theory]
    [InlineData(UsageJournalMode.Spill)]
    [InlineData(UsageJournalMode.Always)]
    public async Task During_a_database_outage_usage_goes_to_the_journal_and_is_written_once_afterwards(UsageJournalMode mode)
    {
        var key = await fixture.CreateKeyAsync();
        var records = Enumerable.Range(0, 3).Select(_ => new UsageRecord
        {
            RequestId = Guid.NewGuid().ToString("N"), VirtualKeyId = key.Id, TeamId = key.TeamId,
            DepartmentId = fixture.DepartmentId, Timestamp = DateTimeOffset.UtcNow, RequestedModel = "test", StatusCode = 200,
        }).ToList();
        var fail = 1;
        var scopes = new OutageScopeFactory(fixture.Services, () => Volatile.Read(ref fail) == 1);
        var journal = new InMemoryUsageJournal(TimeProvider.System);
        var options = Options.Create(new GatewayOptions { UsageJournal = new UsageJournalOptions { Mode = mode } });
        using var writer = new UsageWriter(scopes, fixture.Services.GetRequiredService<AlertNotifier>(), TimeProvider.System,
            NullLogger<UsageWriter>.Instance, fixture.Services.GetRequiredService<GatewayMetrics>(), journal, options);
        await writer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            foreach (var record in records)
            {
                await writer.EnqueueAsync(new UsageWork(record, []), deadline.Token);
            }

            // The writer keeps draining the queue into the journal instead of holding batches in memory.
            while (writer.Status.ConsecutiveFailures == 0 || writer.QueueDepth > 0 || writer.Status.InFlightRecords > 0
                   || await journal.CountRecordsAsync(deadline.Token) < records.Count)
            {
                await Task.Delay(10, deadline.Token);
            }

            writer.Status.JournaledBatches.ShouldBeGreaterThan(0);

            Volatile.Write(ref fail, 0);
            await writer.FlushAsync(deadline.Token); // waits for the journal too
            writer.Status.JournaledBatches.ShouldBe(0);
            (await journal.CountRecordsAsync(deadline.Token)).ShouldBe(0);
            var ids = records.Select(r => r.RequestId).ToArray();
            using var scope = fixture.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.CountAsync(r => ids.Contains(r.RequestId),
                deadline.Token)).ShouldBe(records.Count);
        }
        finally { await writer.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task Always_mode_journals_every_batch_and_clears_it_once_written()
    {
        var key = await fixture.CreateKeyAsync();
        var record = new UsageRecord
        {
            RequestId = Guid.NewGuid().ToString("N"), VirtualKeyId = key.Id, TeamId = key.TeamId,
            DepartmentId = fixture.DepartmentId, Timestamp = DateTimeOffset.UtcNow, RequestedModel = "test", StatusCode = 200,
        };
        var journal = new CountingJournal(new InMemoryUsageJournal(TimeProvider.System));
        var options = Options.Create(new GatewayOptions { UsageJournal = new UsageJournalOptions { Mode = UsageJournalMode.Always } });
        using var writer = new UsageWriter(fixture.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Services.GetRequiredService<AlertNotifier>(),
            TimeProvider.System, NullLogger<UsageWriter>.Instance, fixture.Services.GetRequiredService<GatewayMetrics>(), journal, options);
        await writer.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await writer.EnqueueAsync(new UsageWork(record, []), TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
            journal.Added.ShouldBe(1);
            (await journal.CountRecordsAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        }
        finally { await writer.StopAsync(TestContext.Current.CancellationToken); }
    }

    private sealed class CountingJournal(IUsageJournal inner) : IUsageJournal
    {
        public int Added;
        public bool IsDurable => inner.IsDurable;
        public Task<bool> AddAsync(string batch, ReadOnlyMemory<byte> payload, int records, long maxRecords, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Added);
            return inner.AddAsync(batch, payload, records, maxRecords, cancellationToken);
        }

        public Task<byte[]?> GetAsync(string batch, CancellationToken cancellationToken) => inner.GetAsync(batch, cancellationToken);
        public Task RemoveAsync(string batch, CancellationToken cancellationToken) => inner.RemoveAsync(batch, cancellationToken);
        public Task TouchAsync(IReadOnlyCollection<string> batches, CancellationToken cancellationToken) => inner.TouchAsync(batches, cancellationToken);
        public Task<IReadOnlyList<string>> ClaimAbandonedAsync(TimeSpan idle, int max, CancellationToken cancellationToken) => inner.ClaimAbandonedAsync(idle, max, cancellationToken);
        public Task<long> CountRecordsAsync(CancellationToken cancellationToken) => inner.CountRecordsAsync(cancellationToken);
    }

    private HttpRequestMessage Signed(long timestamp, string? signature = null)
    {
        var pepper = fixture.Services.GetRequiredService<IOptions<GatewaySecurityOptions>>().Value.KeyPepper;
        var request = new HttpRequestMessage(HttpMethod.Get, OperationsSignature.Path);
        request.Headers.Add(OperationsSignature.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(OperationsSignature.SignatureHeader, signature ?? OperationsSignature.Sign(pepper, timestamp));
        return request;
    }

    private sealed class OutageScopeFactory(IServiceProvider services, Func<bool> outage) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => outage()
            ? throw new InvalidOperationException("Simulated database outage") : services.CreateScope();
    }
}
