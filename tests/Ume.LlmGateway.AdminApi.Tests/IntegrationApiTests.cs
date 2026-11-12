using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>The Integration API (<c>/integration/v1</c>, docs/integrations.md): credential keys with scopes read their integration's usage and budgets.</summary>
public sealed class IntegrationApiTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(Guid Integration, Guid Team, Guid Department, string Credential, Guid CredentialId, Guid Chatbot, string OtherCredential, Guid OtherChatbot);

    [Fact]
    public async Task Usage_is_the_integrations_own_keys_per_day_key_model_and_step()
    {
        var s = await SetupAsync();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime);
        await AddUsageAsync(s.Chatbot, s.Team, s.Department, "eu/chat", "answer", "run-1", 0.10m);
        await AddUsageAsync(s.Chatbot, s.Team, s.Department, "eu/chat", "answer", "run-2", 0.20m);
        await AddUsageAsync(s.Chatbot, s.Team, s.Department, "eu/embed", "embed", "run-1", 0.01m);
        await AddUsageAsync(s.OtherChatbot, s.Team, s.Department, "eu/chat", "answer", "run-1", 5m); // another integration's

        using var client = await KeyClientAsync(s.Credential);
        var day = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        using (var byKey = await client.GetAsync($"/integration/v1/usage?{day}", Ct))
        {
            var body = await AdminFixture.ReadAsync(byKey);
            var row = body["items"]!.AsArray().Single()!;
            row["keyId"]!.GetValue<Guid>().ShouldBe(s.Chatbot);
            row["keyName"]!.GetValue<string>().ShouldBe("Chatbot");
            row["requests"]!.GetValue<long>().ShouldBe(3);
            row["costUsd"]!.GetValue<decimal>().ShouldBe(0.31m);
            row["model"].ShouldBeNull();
        }

        using (var byStep = await client.GetAsync($"/integration/v1/usage?{day}&groupBy=model,step", Ct))
        {
            var rows = (await AdminFixture.ReadAsync(byStep))["items"]!.AsArray();
            rows.Select(r => (r!["model"]!.GetValue<string>(), r["step"]!.GetValue<string>(), r["costUsd"]!.GetValue<decimal>()))
                .ShouldBe([("eu/chat", "answer", 0.30m), ("eu/embed", "embed", 0.01m)], ignoreOrder: true);
            rows.ShouldAllBe(r => r!["keyId"] == null);
        }

        using var run = await client.GetAsync($"/integration/v1/usage?{day}&runId=run-1", Ct);
        (await AdminFixture.ReadAsync(run))["items"]!.AsArray().Single()!["costUsd"]!.GetValue<decimal>().ShouldBe(0.11m);

        // Another integration's key is not found, and its usage was never counted above.
        (await client.GetAsync($"/integration/v1/usage?{day}&keyId={s.OtherChatbot}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync($"/integration/v1/usage?{day}&groupBy=team", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync($"/integration/v1/usage?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Budgets_are_those_the_gateway_applies_to_the_key_with_what_they_used()
    {
        var s = await SetupAsync();
        using var admin = await fixture.ClientAsync();
        foreach (var (scope, id, limit) in new[] { ("VirtualKey", s.Chatbot, 1m), ("Team", s.Team, 100m), ("Department", s.Department, 1000m) })
        {
            using var created = await admin.PostAsJsonAsync("/api/budgets", new { scope, scopeId = id, limitUsd = limit, period = "Monthly", alertThresholds = new[] { 80 } }, Ct);
            await AdminFixture.ReadAsync(created, HttpStatusCode.Created);
        }

        await AddUsageAsync(s.Chatbot, s.Team, s.Department, "eu/chat", "answer", null, 1.5m);

        using var client = await KeyClientAsync(s.Credential);
        using var response = await client.GetAsync($"/integration/v1/budgets?keyId={s.Chatbot}", Ct);
        var items = (await AdminFixture.ReadAsync(response))["items"]!.AsArray();
        items.Select(b => b!["scope"]!.GetValue<string>()).ShouldBe(["Department", "Team", "VirtualKey"], ignoreOrder: true);
        var own = items.Single(b => b!["scope"]!.GetValue<string>() == "VirtualKey")!;
        own["spent"]!.GetValue<decimal>().ShouldBe(1.5m);
        own["exhausted"]!.GetValue<bool>().ShouldBeTrue();
        items.Single(b => b!["scope"]!.GetValue<string>() == "Team")!["exhausted"]!.GetValue<bool>().ShouldBeFalse();

        (await client.GetAsync($"/integration/v1/budgets?keyId={s.OtherChatbot}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_a_usable_credential_key_with_the_scope_gets_in_and_never_through_a_session()
    {
        var s = await SetupAsync();
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        using var plain = await admin.PostAsJsonAsync("/api/keys", Key(org.Team), Ct);
        var ordinary = (await AdminFixture.ReadAsync(plain, HttpStatusCode.Created))["secret"]!.GetValue<string>();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var path = $"/integration/v1/usage?from={today}&to={today}";

        (await (await KeyClientAsync(null)).GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await KeyClientAsync("ume-sk-not-a-key")).GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await KeyClientAsync(ordinary)).GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await (await KeyClientAsync(s.OtherCredential)).GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // no scope
        (await admin.GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // a gateway admin's session is not a key

        (await admin.PostAsync($"/api/keys/{s.CredentialId}/revoke", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await KeyClientAsync(s.Credential)).GetAsync(path, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>Two integrations: the first with a credential key holding usage.read and a chatbot key, the second without the scope.</summary>
    private async Task<Setup> SetupAsync()
    {
        using var admin = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(admin);
        async Task<(Guid Integration, string Credential, Guid CredentialId, Guid Chatbot)> OneAsync(string[] scopes)
        {
            using var created = await admin.PostAsJsonAsync("/api/integrations", new { name = "Platform " + Guid.NewGuid().ToString("N")[..8], ownerTeamId = org.Team }, Ct);
            var integration = (await AdminFixture.ReadAsync(created, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
            using var credential = await admin.PostAsJsonAsync("/api/keys", Key(org.Team, integration, "Credential", scopes), Ct);
            var credentialBody = await AdminFixture.ReadAsync(credential, HttpStatusCode.Created);
            using var chatbot = await admin.PostAsJsonAsync("/api/keys", Key(org.Team, integration, "Assigned", null, "Chatbot"), Ct);
            var chatbotId = (await AdminFixture.ReadAsync(chatbot, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
            return (integration, credentialBody["secret"]!.GetValue<string>(), credentialBody["key"]!["id"]!.GetValue<Guid>(), chatbotId);
        }

        var first = await OneAsync(["usage.read"]);
        var second = await OneAsync([]);
        return new Setup(first.Integration, org.Team, org.Department, first.Credential, first.CredentialId, first.Chatbot, second.Credential, second.Chatbot);
    }

    private async Task<HttpClient> KeyClientAsync(string? key)
    {
        var client = await fixture.ClientAsync(role: null);
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return client;
    }

    private async Task AddUsageAsync(Guid key, Guid team, Guid department, string model, string? step, string? runId, decimal cost)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.UsageRecords.Add(new UsageRecord
        {
            RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = DateTimeOffset.UtcNow, VirtualKeyId = key, TeamId = team, DepartmentId = department,
            Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = model, InputTokens = 10, OutputTokens = 5, CostUsd = cost, StatusCode = 200,
            Outcome = RequestOutcome.Success, Step = step, RunId = runId,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static object Key(Guid team, Guid? integration = null, string? role = null, string[]? scopes = null, string name = "Key") => new
    {
        teamId = team, name, allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Off",
        requestsPerMinute = 60, integrationId = integration, integrationRole = role, integrationScopes = scopes,
    };
}
