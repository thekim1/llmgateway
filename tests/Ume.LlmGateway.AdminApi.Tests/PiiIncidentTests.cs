using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class PiiIncidentTests(AdminFixture fixture)
{
    [Fact]
    public async Task Incidents_are_filtered_summarised_and_department_scoped()
    {
        using var admin = await fixture.ClientAsync();
        var own = await fixture.OrganisationAsync(admin);
        var other = await fixture.OrganisationAsync(admin);
        var key = await KeyAsync(admin, own.Team);
        var otherKey = await KeyAsync(admin, other.Team);
        var now = DateTimeOffset.UtcNow;
        var redacted = await AddAsync(key, own, now.AddMinutes(-3), PiiPolicy.Redact, "Personnummer:2,Email:1", RequestOutcome.Success);
        var allowed = await AddAsync(key, own, now.AddMinutes(-2), PiiPolicy.Allow, "Phone:1", RequestOutcome.Success);
        var blocked = await AddAsync(key, own, now.AddMinutes(-1), PiiPolicy.Block, "Personnummer:1", RequestOutcome.PiiBlocked);
        await AddAsync(key, own, now.AddMinutes(-1), null, null, RequestOutcome.Success); // nothing found: not an incident
        await AddAsync(otherKey, other, now.AddMinutes(-1), PiiPolicy.Block, "Iban:1", RequestOutcome.PiiBlocked);

        using var viewer = await fixture.ClientAsync("viewer", own.Code);
        var report = await GetAsync(viewer, $"keyId={key}");
        report["total"]!.GetValue<int>().ShouldBe(3);
        report["totalIncidents"]!.GetValue<long>().ShouldBe(3);
        var items = report["items"]!.AsArray();
        items.Select(i => i!["requestId"]!.GetValue<string>()).ShouldBe([blocked, allowed, redacted]); // newest first
        var first = items[2]!;
        first["action"]!.GetValue<string>().ShouldBe("Redact");
        first["keyName"]!.GetValue<string>().ShouldBe("Pii key");
        first["teamId"]!.GetValue<Guid>().ShouldBe(own.Team);
        first["endpoint"]!.GetValue<string>().ShouldBe("/v1/chat/completions");
        first["categories"]!.AsArray().Select(c => $"{c!["category"]}:{c["count"]}").ShouldBe(["Personnummer:2", "Email:1"]);

        var categories = report["categories"]!.AsArray().ToDictionary(c => c!["category"]!.GetValue<string>());
        categories["Personnummer"]!["requests"]!.GetValue<long>().ShouldBe(2);
        categories["Personnummer"]!["occurrences"]!.GetValue<long>().ShouldBe(3);
        categories["Email"]!["requests"]!.GetValue<long>().ShouldBe(1);
        categories.ShouldNotContainKey("Iban");
        report["actions"]!.AsArray().Select(a => $"{a!["action"]}:{a["requests"]}").ShouldBe(["Allow:1", "Redact:1", "Block:1"]);

        var personnummer = await GetAsync(viewer, $"keyId={key}&category=personnummer");
        personnummer["total"]!.GetValue<int>().ShouldBe(2);
        personnummer["categories"]!.AsArray().Count.ShouldBe(3); // the tiles ignore the category and action filters
        var allow = await GetAsync(viewer, $"keyId={key}&action=allow");
        allow["items"]!.AsArray().ShouldHaveSingleItem()!["requestId"]!.GetValue<string>().ShouldBe(allowed);
        (await GetAsync(viewer, $"keyId={key}&category=email&action=block"))["total"]!.GetValue<int>().ShouldBe(0);
        (await GetAsync(viewer, $"departmentId={own.Department}&teamId={own.Team}"))["total"]!.GetValue<int>().ShouldBe(3);
        (await GetAsync(viewer, $"keyId={key}&page=2&pageSize=2"))["items"]!.AsArray().Count.ShouldBe(1);

        // Another department's incidents are invisible, also when asked for by key.
        (await GetAsync(viewer, $"keyId={otherKey}"))["total"]!.GetValue<int>().ShouldBe(0);
        using var outsider = await fixture.ClientAsync("viewer", other.Code);
        (await GetAsync(outsider, $"keyId={key}"))["total"]!.GetValue<int>().ShouldBe(0);
    }

    [Theory]
    [InlineData("category=ssn")]
    [InlineData("action=off")]
    [InlineData("action=1")]
    [InlineData("pageSize=500")]
    public async Task Invalid_filters_are_rejected(string query)
    {
        using var client = await fixture.ClientAsync();
        using var response = await client.GetAsync("/api/usage/pii-incidents?" + query, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Category_summary_is_parsed_and_unknown_parts_are_skipped() =>
        PiiIncidentService.ParseCategories("Personnummer:2,Bogus:1,Email,Phone:x, Iban:3").Select(f => $"{f.Category}:{f.Count}")
            .ShouldBe(["Personnummer:2", "Email:1", "Iban:3"]);

    private static async Task<JsonNode> GetAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/usage/pii-incidents?" + query, TestContext.Current.CancellationToken);
        return await AdminFixture.ReadAsync(response);
    }

    private static async Task<Guid> KeyAsync(HttpClient client, Guid team)
    {
        using var response = await client.PostAsJsonAsync("/api/keys", new
        {
            teamId = team, name = "Pii key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Redact",
        }, TestContext.Current.CancellationToken);
        return (await AdminFixture.ReadAsync(response, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
    }

    private async Task<string> AddAsync(Guid key, (Guid Department, Guid Team, string Code) owner, DateTimeOffset at, PiiPolicy? action, string? categories, RequestOutcome outcome)
    {
        var requestId = "req_" + Guid.NewGuid().ToString("N");
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.UsageRecords.Add(new UsageRecord
        {
            RequestId = requestId, Timestamp = at, VirtualKeyId = key, TeamId = owner.Team, DepartmentId = owner.Department,
            Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = "test/model", Outcome = outcome,
            StatusCode = outcome == RequestOutcome.Success ? 200 : 422, PiiActionApplied = action, PiiCategories = categories,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return requestId;
    }
}
