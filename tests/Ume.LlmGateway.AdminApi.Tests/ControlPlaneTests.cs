using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>Budget spend, audit snapshots, invalidations, error responses and headers.</summary>
public sealed class ControlPlaneTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Budget_list_spend_covers_rotated_keys_teams_and_departments()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var create = await client.PostAsJsonAsync("/api/keys", KeyBody(org.Team), Ct);
        var oldKey = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        using var rotate = await client.PostAsJsonAsync($"/api/keys/{oldKey}/rotate", new { mode = "Grace24Hours" }, Ct);
        var newKey = (await AdminFixture.ReadAsync(rotate))["key"]!["id"]!.GetValue<Guid>();

        await UsageAsync(org, oldKey, 1.25m, DateTimeOffset.UtcNow);
        await UsageAsync(org, newKey, 2m, DateTimeOffset.UtcNow);
        await UsageAsync(org, newKey, 100m, DateTimeOffset.UtcNow.AddYears(-2)); // outside every current period

        async Task<Guid> BudgetAsync(string scope, Guid scopeId, string period)
        {
            using var response = await client.PostAsJsonAsync("/api/budgets", new { scope, scopeId, limitSek = 10, period, alertThresholds = new[] { 100 }, isActive = true }, Ct);
            var budget = await AdminFixture.ReadAsync(response, HttpStatusCode.Created);
            budget["spentSek"]!.GetValue<decimal>().ShouldBe(3.25m); // the single-budget query agrees with the batched list
            return budget["id"]!.GetValue<Guid>();
        }
        var keyBudget = await BudgetAsync("VirtualKey", oldKey, "Monthly");
        var teamBudget = await BudgetAsync("Team", org.Team, "Yearly");
        var departmentBudget = await BudgetAsync("Department", org.Department, "Monthly");

        using var list = await client.GetAsync("/api/budgets", Ct);
        var own = (await AdminFixture.ReadAsync(list)).AsArray().Where(b => b!["id"]!.GetValue<Guid>() is var id && (id == keyBudget || id == teamBudget || id == departmentBudget)).ToList();
        own.Count.ShouldBe(3);
        own.ShouldAllBe(b => b!["spentSek"]!.GetValue<decimal>() == 3.25m);
        own.ShouldAllBe(b => b!["percentUsed"]!.GetValue<decimal>() == 32.5m);
        own.Single(b => b!["id"]!.GetValue<Guid>() == keyBudget)!["scopeName"]!.GetValue<string>().ShouldBe("Key");
        own.Single(b => b!["id"]!.GetValue<Guid>() == departmentBudget)!["scopeName"]!.GetValue<string>().ShouldStartWith("Department ");

        // A department admin of another department sees none of them.
        using var other = await fixture.ClientAsync("department-admin", Guid.NewGuid().ToString("N"));
        using var hidden = await other.GetAsync("/api/budgets", Ct);
        (await AdminFixture.ReadAsync(hidden)).AsArray().ShouldNotContain(b => b!["id"]!.GetValue<Guid>() == keyBudget);
    }

    [Fact]
    public async Task Audit_details_are_snapshots_of_stored_settings()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N");
        using var providerResponse = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "audit-" + suffix, baseUrl = "https://provider.invalid/v1", type = "OpenAICompatible", authMode = "Bearer", credential = "secret-" + suffix,
            residency = "Eu", capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 10, isEnabled = true,
        }, Ct);
        var providerId = (await AdminFixture.ReadAsync(providerResponse, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        using var modelResponse = await client.PostAsJsonAsync("/api/models", new
        {
            providerId, name = "audit/model-" + suffix, upstreamModel = "m", kind = "Chat", parameterProfile = "Standard", isEnabled = true,
            price = new { inputPerMillionUsd = 1, cachedInputPerMillionUsd = 0, outputPerMillionUsd = 2 },
        }, Ct);
        var modelId = (await AdminFixture.ReadAsync(modelResponse, HttpStatusCode.Created))["id"]!.GetValue<Guid>();

        var provider = await AuditAsync(providerId, "create");
        provider.GetProperty("before").ValueKind.ShouldBe(JsonValueKind.Null);
        var after = provider.GetProperty("after");
        after.GetProperty("name").GetString().ShouldBe("audit-" + suffix);
        after.GetProperty("residency").GetString().ShouldBe("Eu");
        after.GetProperty("hasCredential").GetBoolean().ShouldBeTrue();
        after.TryGetProperty("deploymentCount", out _).ShouldBeFalse(); // a loaded navigation, not a setting
        after.TryGetProperty("createdAt", out _).ShouldBeFalse();
        provider.GetRawText().ShouldNotContain("secret-" + suffix);

        var model = (await AuditAsync(modelId, "create")).GetProperty("after");
        model.GetProperty("providerId").GetGuid().ShouldBe(providerId);
        model.GetProperty("kind").GetString().ShouldBe("Chat");
        model.TryGetProperty("currentPrice", out _).ShouldBeFalse(); // depends on the clock
        model.TryGetProperty("providerName", out _).ShouldBeFalse();

        var org = await fixture.OrganisationAsync(client);
        using var key = await client.PostAsJsonAsync("/api/keys", KeyBody(org.Team), Ct);
        var keyId = (await AdminFixture.ReadAsync(key, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();
        var keyAudit = (await AuditAsync(keyId, "create")).GetProperty("after");
        keyAudit.GetProperty("piiPolicy").GetString().ShouldBe("Block");
        keyAudit.TryGetProperty("status", out _).ShouldBeFalse();
        keyAudit.TryGetProperty("lastUsedAt", out _).ShouldBeFalse();

        using var deleteModel = await client.DeleteAsync($"/api/models/{modelId}", Ct);
        deleteModel.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var deleteProvider = await client.DeleteAsync($"/api/providers/{providerId}", Ct);
        deleteProvider.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task One_save_publishes_every_affected_cache_without_extra_audit_rows()
    {
        using var client = await fixture.ClientAsync();
        var org = await fixture.OrganisationAsync(client);
        using var create = await client.PostAsJsonAsync("/api/keys", KeyBody(org.Team), Ct);
        var keyId = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["key"]!["id"]!.GetValue<Guid>();

        var changes = new List<InvalidationKind>();
        using (fixture.Services.GetRequiredService<IInvalidationBus>().Subscribe(kind => { lock (changes) { changes.Add(kind); } }))
        {
            using var update = await client.PutAsJsonAsync($"/api/departments/{org.Department}", new { name = "Renamed " + org.Code, costCenterCode = org.Code, isActive = true }, Ct);
            update.StatusCode.ShouldBe(HttpStatusCode.OK);
            lock (changes) { changes.ShouldBe([InvalidationKind.Config, InvalidationKind.Keys], ignoreOrder: true); changes.Clear(); }

            using var rotate = await client.PostAsJsonAsync($"/api/keys/{keyId}/rotate", new { mode = "RevokeImmediately" }, Ct);
            rotate.StatusCode.ShouldBe(HttpStatusCode.OK);
            lock (changes) { changes.ShouldBe([InvalidationKind.Keys, InvalidationKind.Config], ignoreOrder: true); }
        }

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var ids = new[] { org.Department.ToString(), keyId.ToString() };
        var actions = await db.AuditLog.Where(a => ids.Contains(a.EntityId)).Select(a => a.Action).ToListAsync(Ct);
        actions.ShouldNotContain(AuditActions.Invalidate);
        actions.Count(a => a == AuditActions.Rotate).ShouldBe(1);
    }

    [Fact]
    public async Task Faults_are_problem_responses_with_the_security_headers()
    {
        using var client = await fixture.ClientAsync();
        using var missing = await client.GetAsync($"/api/keys/{Guid.NewGuid()}", Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await AdminFixture.ReadAsync(missing, HttpStatusCode.NotFound);
        problem["status"]!.GetValue<int>().ShouldBe(404);
        problem["title"]!.GetValue<string>().ShouldBe("Åtgärden kunde inte utföras");
        problem["detail"]!.GetValue<string>().ShouldBe("Nyckeln finns inte.");
        // The exception handler clears the response; the headers are applied again.
        missing.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        missing.Headers.CacheControl!.NoStore.ShouldBeTrue();
        missing.Headers.Contains("Content-Security-Policy").ShouldBeTrue();

        var org = await fixture.OrganisationAsync(client);
        using var duplicate = await client.PostAsJsonAsync("/api/departments", new { name = "Duplicate", costCenterCode = org.Code }, Ct);
        (await AdminFixture.ReadAsync(duplicate, HttpStatusCode.Conflict))["detail"]!.GetValue<string>().ShouldBe("Ändringen krockar med befintliga uppgifter.");

        using var badGrouping = await client.GetAsync("/api/usage/summary?groupBy=week", Ct);
        (await AdminFixture.ReadAsync(badGrouping, HttpStatusCode.BadRequest))["detail"]!.GetValue<string>().ShouldBe("Grupperingen är ogiltig.");
        using var upperCaseGrouping = await client.GetAsync("/api/usage/summary?groupBy=Team", Ct);
        upperCaseGrouping.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var invalidRule = await client.PostAsJsonAsync("/api/routing-rules", new
        {
            name = "Faulty " + Guid.NewGuid(), scope = "Global", targets = new[] { new { model = "no-such-model-" + Guid.NewGuid(), weight = 1 } },
        }, Ct);
        var validation = await AdminFixture.ReadAsync(invalidRule, HttpStatusCode.BadRequest);
        validation["title"]!.GetValue<string>().ShouldBe("Kontrollera de markerade fälten.");
        validation["errors"]!["targets"].ShouldNotBeNull();
    }

    // The usage report shows these paths; they are part of the admin API output.
    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, "/v1/chat/completions")]
    [InlineData(GatewayEndpoint.Embeddings, "/v1/embeddings")]
    [InlineData(GatewayEndpoint.Responses, "/v1/responses")]
    [InlineData(GatewayEndpoint.AnthropicMessages, "/v1/messages")]
    [InlineData(GatewayEndpoint.Models, "/v1/models")]
    [InlineData(GatewayEndpoint.AudioTranscriptions, "/v1/audio/transcriptions")]
    [InlineData(GatewayEndpoint.AudioTranslations, "/v1/audio/translations")]
    [InlineData(GatewayEndpoint.Realtime, "/v1/realtime")]
    [InlineData(GatewayEndpoint.RealtimeTranslations, "/v1/realtime/translations")]
    public void Every_endpoint_has_a_path(GatewayEndpoint endpoint, string path) => GatewayEndpoints.Info(endpoint).ClientPath.ShouldBe(path);

    [Fact]
    public void No_endpoint_lacks_a_path() =>
        Enum.GetValues<GatewayEndpoint>().ShouldAllBe(e => GatewayEndpoints.Info(e).ClientPath.StartsWith("/v1/", StringComparison.Ordinal));

    private async Task<JsonElement> AuditAsync(Guid entityId, string action)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var details = await db.AuditLog.Where(a => a.EntityId == entityId.ToString() && a.Action == action).Select(a => a.Details).SingleAsync(Ct);
        return JsonDocument.Parse(details!).RootElement.Clone();
    }

    private async Task UsageAsync((Guid Department, Guid Team, string Code) org, Guid key, decimal cost, DateTimeOffset at)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.UsageRecords.Add(new UsageRecord
        {
            RequestId = "req_" + Guid.NewGuid().ToString("N"), Timestamp = at, VirtualKeyId = key, TeamId = org.Team, DepartmentId = org.Department,
            Endpoint = GatewayEndpoint.ChatCompletions, RequestedModel = "test/model", CostSek = cost, Outcome = RequestOutcome.Success, StatusCode = 200,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static object KeyBody(Guid team) => new { teamId = team, name = "Key", allowedModels = Array.Empty<string>(), allowedResidencies = Array.Empty<string>(), piiPolicy = "Block" };
}
