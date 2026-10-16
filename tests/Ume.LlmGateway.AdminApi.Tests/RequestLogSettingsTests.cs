using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class RequestLogSettingsTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_see_or_change_request_logging(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var read = await client.GetAsync("/api/settings/request-log", Ct);
        ((int)read.StatusCode).ShouldBe(status);
        using var write = await client.PutAsJsonAsync("/api/settings/request-log", new { logAllRequests = true, noTimeLimit = true }, Ct);
        ((int)write.StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task Request_logging_turns_on_for_24_hours_or_without_limit_audited_and_published()
    {
        using var client = await fixture.ClientAsync();
        var changes = new List<InvalidationKind>();
        using var subscription = fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(changes.Add);

        var before = DateTimeOffset.UtcNow;
        using var day = await client.PutAsJsonAsync("/api/settings/request-log", new { logAllRequests = true, noTimeLimit = false }, Ct);
        var saved = await AdminFixture.ReadAsync(day);
        saved["active"]!.GetValue<bool>().ShouldBeTrue();
        saved["until"]!.GetValue<DateTimeOffset>().ShouldBeInRange(before.AddHours(24).AddSeconds(-5), DateTimeOffset.UtcNow.AddHours(24).AddSeconds(5));
        changes.ShouldContain(InvalidationKind.Config);

        using var unlimited = await client.PutAsJsonAsync("/api/settings/request-log", new { logAllRequests = true, noTimeLimit = true }, Ct);
        var always = await AdminFixture.ReadAsync(unlimited);
        always["active"]!.GetValue<bool>().ShouldBeTrue();
        always["until"].ShouldBeNull();

        using var read = await client.GetAsync("/api/settings/request-log", Ct);
        (await AdminFixture.ReadAsync(read))["active"]!.GetValue<bool>().ShouldBeTrue();

        using var off = await client.PutAsJsonAsync("/api/settings/request-log", new { logAllRequests = false, noTimeLimit = true }, Ct);
        var stopped = await AdminFixture.ReadAsync(off);
        stopped["active"]!.GetValue<bool>().ShouldBeFalse();
        stopped["until"].ShouldBeNull();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        // Retention and network tests write settings entries in parallel, so the newest one need not be ours.
        var audits = await db.AuditLog.Where(a => a.EntityType == AuditEntities.Settings).OrderByDescending(a => a.Id).Take(50).ToListAsync(Ct);
        audits.ShouldContain(a => a.Details != null && a.Details.Contains("LogAllRequests", StringComparison.OrdinalIgnoreCase));
    }
}
