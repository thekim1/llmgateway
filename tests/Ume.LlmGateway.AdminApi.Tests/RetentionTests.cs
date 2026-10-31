using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class RetentionTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_see_change_or_run_retention(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var read = await client.GetAsync("/api/settings/retention", Ct);
        ((int)read.StatusCode).ShouldBe(status);
        using var write = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = 365, auditRetentionDays = 365 }, Ct);
        ((int)write.StatusCode).ShouldBe(status);
        using var run = await client.PostAsync("/api/settings/retention/run", null, Ct);
        ((int)run.StatusCode).ShouldBe(status);
    }

    [Theory]
    [InlineData(89, 365)]
    [InlineData(3651, 365)]
    [InlineData(365, 30)]
    public async Task Retention_below_90_days_or_above_ten_years_is_refused(int usage, int audit)
    {
        using var client = await fixture.ClientAsync();
        using var response = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = usage, auditRetentionDays = audit }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_round_trip_with_audit_kept_for_ever()
    {
        using var client = await fixture.ClientAsync();
        try
        {
            using var save = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = 400, auditRetentionDays = (int?)null }, Ct);
            var saved = await AdminFixture.ReadAsync(save);
            saved["usageRetentionDays"]!.GetValue<int>().ShouldBe(400);
            saved["auditRetentionDays"].ShouldBeNull();
            saved["nextAuditCutoff"].ShouldBeNull();
            // Rows already removed stay removed, so a retention run by another test (the database is shared) can push the cutoff later.
            var cutoff = RetentionPolicy.UsageCutoff(DateTimeOffset.UtcNow, 400);
            var retainedFrom = saved["usageRetainedFrom"]?.GetValue<DateTimeOffset>();
            saved["nextUsageCutoff"]!.GetValue<DateTimeOffset>().ShouldBe(retainedFrom is { } kept && kept > cutoff ? kept : cutoff, TimeSpan.FromDays(1));

            using var read = await client.GetAsync("/api/settings/retention", Ct);
            (await AdminFixture.ReadAsync(read))["auditRetentionDays"].ShouldBeNull();
        }
        finally
        {
            using var reset = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = 365, auditRetentionDays = 365 }, Ct);
            reset.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Run_keeps_monthly_totals_removes_old_rows_and_reports_still_add_up()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var created = await client.PostAsJsonAsync("/api/keys", new
        {
            teamId = org.Team, name = "Retention", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
        }, Ct);
        var keyId = (await AdminFixture.ReadAsync(created, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();

        var now = DateTimeOffset.UtcNow;
        var oldMonth = BudgetPeriods.GetWindow(BudgetPeriod.Monthly, now.AddDays(-200)).Start;
        var auditId = Guid.NewGuid().ToString();
        var prefix = "ret-" + auditId[..8];
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            db.UsageRecords.AddRange(
                Usage(org, keyId, oldMonth.AddDays(1), 1.5m, RequestOutcome.Success, fallbacks: 1),
                Usage(org, keyId, oldMonth.AddDays(5), 2.5m, RequestOutcome.ProviderError, fallbacks: 0),
                Usage(org, keyId, now.AddDays(-5), 4m, RequestOutcome.Success, fallbacks: 0));
            db.AuthFailures.Add(new AuthFailure
            {
                FirstSeen = now.AddDays(-200), LastSeen = now.AddDays(-200), Reason = AuthFailureReason.InvalidKey, Endpoint = GatewayEndpoint.ChatCompletions,
                KeyPrefix = prefix, Count = 1,
            });
            db.AlertEvents.Add(new AlertEvent
            {
                BudgetId = Guid.NewGuid(), Scope = BudgetScope.VirtualKey, ScopeId = keyId, PeriodStart = now.AddDays(-500), ThresholdPercent = 80,
                SpentUsd = 80, LimitUsd = 100, Timestamp = now.AddDays(-480),
            });
            db.AuditLog.Add(new AuditLogEntry { Timestamp = now.AddDays(-200), Actor = "test", Action = "update", EntityType = "Test", EntityId = auditId });
            await db.SaveChangesAsync(Ct);
        }

        try
        {
            using (var save = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = 90, auditRetentionDays = 90 }, Ct))
            {
                save.EnsureSuccessStatusCode();
            }

            (await fixture.Services.GetRequiredService<RetentionService>().RunOnceAsync(Ct)).ShouldBeGreaterThanOrEqualTo(5);

            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                (await db.UsageRecords.Where(u => u.VirtualKeyId == keyId).Select(u => u.CostUsd).ToListAsync(Ct)).ShouldBe([4m]);
                var total = await db.UsageMonthlyTotals.SingleAsync(t => t.VirtualKeyId == keyId, Ct);
                total.MonthStart.ShouldBe(oldMonth);
                (total.Requests, total.SuccessfulRequests, total.Fallbacks, total.CostUsd).ShouldBe((2L, 1L, 1L, 4m));
                (await db.AuthFailures.AnyAsync(f => f.KeyPrefix == prefix, Ct)).ShouldBeFalse();
                (await db.AlertEvents.AnyAsync(a => a.ScopeId == keyId, Ct)).ShouldBeFalse();
                (await db.AuditLog.AnyAsync(a => a.EntityId == auditId, Ct)).ShouldBeFalse();

                // Budget spend over a window that starts in the removed month adds totals and records: exact.
                var kept = await db.UsageRetainedFromAsync(Ct);
                kept.ShouldNotBeNull();
                kept.Value.ShouldBe(RetentionPolicy.UsageCutoff(now, 90), TimeSpan.FromDays(1));
                (await db.CostsIn(new PeriodWindow(oldMonth, now.AddMinutes(1)), kept).Where(c => c.VirtualKeyId == keyId).SumAsync(c => c.CostUsd, Ct)).ShouldBe(8m);
            }

            using (var status = await client.GetAsync("/api/settings/retention", Ct))
            {
                var dto = await AdminFixture.ReadAsync(status);
                dto["lastRunAt"].ShouldNotBeNull();
                dto["lastRunError"].ShouldBeNull();
                dto["usageRetainedFrom"].ShouldNotBeNull();
            }

            // Reports reaching back before the oldest record read the month's totals.
            var from = Uri.EscapeDataString(now.AddDays(-250).ToString("O"));
            using (var summary = await client.GetAsync($"/api/usage/summary?groupBy=key&departmentId={org.Department}&from={from}", Ct))
            {
                var dto = await AdminFixture.ReadAsync(summary);
                dto["totalRequests"]!.GetValue<long>().ShouldBe(3);
                dto["totalCostUsd"]!.GetValue<decimal>().ShouldBe(8m);
                dto["totalErrors"]!.GetValue<long>().ShouldBe(1);
                dto["monthlyTotalsBefore"].ShouldNotBeNull();
            }

            using (var byDay = await client.GetAsync($"/api/usage/summary?groupBy=day&departmentId={org.Department}&from={from}", Ct))
            {
                var month = TimeZoneInfo.ConvertTime(oldMonth, BudgetPeriods.Stockholm).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
                var rows = (await AdminFixture.ReadAsync(byDay))["rows"]!.AsArray();
                rows.ShouldContain(r => r!["label"]!.GetValue<string>() == month && r["requests"]!.GetValue<long>() == 2);
            }

            using (var csv = await client.GetAsync($"/api/usage/export.csv?from={from}", Ct))
            {
                var text = await csv.Content.ReadAsStringAsync(Ct);
                text.ShouldContain("Retention;3;");
            }

            // Audit kept for ever: an old entry survives the next run.
            var keptAudit = Guid.NewGuid().ToString();
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
                db.AuditLog.Add(new AuditLogEntry { Timestamp = now.AddDays(-200), Actor = "test", Action = "update", EntityType = "Test", EntityId = keptAudit });
                await db.SaveChangesAsync(Ct);
            }

            using (var save = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = 90, auditRetentionDays = (int?)null }, Ct))
            {
                save.EnsureSuccessStatusCode();
            }

            await fixture.Services.GetRequiredService<RetentionService>().RunOnceAsync(Ct);
            using (var scope = fixture.Services.CreateScope())
            {
                (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuditLog.AnyAsync(a => a.EntityId == keptAudit, Ct)).ShouldBeTrue();
            }
        }
        finally
        {
            using var reset = await client.PutAsJsonAsync("/api/settings/retention", new { usageRetentionDays = 365, auditRetentionDays = 365 }, Ct);
            using var scope = fixture.Services.CreateScope();
            // Other tests insert usage freely; give them back a database without a retention boundary.
            await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Settings
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsageRetainedFrom, (DateTimeOffset?)null), Ct);
        }
    }

    [Fact]
    public async Task Run_now_starts_in_the_background_and_is_audited()
    {
        using var client = await fixture.ClientAsync();
        var before = DateTimeOffset.UtcNow;
        using var run = await client.PostAsync("/api/settings/retention/run", null, Ct);
        run.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var status = await client.GetAsync("/api/settings/retention", Ct);
            var dto = await AdminFixture.ReadAsync(status);
            if (dto["lastRunAt"] is { } last && last.GetValue<DateTimeOffset>() >= before && !dto["running"]!.GetValue<bool>())
            {
                dto["lastRunError"].ShouldBeNull();
                using var scope = fixture.Services.CreateScope();
                (await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().AuditLog
                    .AnyAsync(a => a.EntityType == AuditEntities.Settings && a.Action == AuditActions.Run && a.Timestamp >= before.AddSeconds(-1), Ct)).ShouldBeTrue();
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("The retention run did not finish.");
    }

    private static UsageRecord Usage((Guid Department, Guid Team, string Code) org, Guid key, DateTimeOffset at, decimal cost, RequestOutcome outcome, int fallbacks) => new()
    {
        RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = at, VirtualKeyId = key, TeamId = org.Team, DepartmentId = org.Department,
        Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = "test/model", ProviderName = "test", InputTokens = 10, OutputTokens = 5,
        CostUsd = cost, Outcome = outcome, StatusCode = outcome == RequestOutcome.Success ? 200 : 503, FallbackCount = fallbacks,
    };
}
