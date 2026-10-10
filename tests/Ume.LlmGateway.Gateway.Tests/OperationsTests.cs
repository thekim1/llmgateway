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
        var writer = fixture.Services.GetRequiredService<UsageWriter>();
        await writer.EnqueueAsync(new UsageWork(record, []), TestContext.Current.CancellationToken);
        await writer.FlushAsync(TestContext.Current.CancellationToken);
        await writer.EnqueueAsync(new UsageWork(record, []), TestContext.Current.CancellationToken);
        await writer.FlushAsync(TestContext.Current.CancellationToken);
        writer.Status.LastWriteAt.ShouldNotBeNull();
        writer.Status.ConsecutiveFailures.ShouldBe(0);
        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.CountAsync(r => r.Id == record.Id,
            TestContext.Current.CancellationToken)).ShouldBe(1);
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
            (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.CountAsync(r => r.Id == record.Id,
                deadline.Token)).ShouldBe(1);
        }
        finally { await writer.StopAsync(TestContext.Current.CancellationToken); }
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
