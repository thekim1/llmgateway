using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

/// <summary>
/// A model's own residency, publisher, request address and authentication (a reranker on Azure AI Foundry): set by the
/// admin, checked, shown, audited and carried by the configuration document.
/// </summary>
public sealed class FoundryRerankAdminTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> ProviderAsync(HttpClient client, string type = "AzureAIFoundry", string baseUrl = "https://umea-res.services.ai.azure.com/api/projects/umea")
    {
        var name = "foundry-" + Guid.NewGuid().ToString("N")[..8];
        using var create = await client.PostAsJsonAsync("/api/providers", new
        {
            name, baseUrl, type, authMode = "ApiKeyHeader", credential = "secret", residency = "External",
            capabilities = new[] { "ChatCompletions" }, timeoutSeconds = 10, isEnabled = true,
        }, Ct);
        return (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    private static object Model(Guid providerId, string name, string kind = "Rerank", string? residency = null, string? publisher = null,
        string? requestBaseUrl = null, string? requestAuthMode = null) => new
    {
        providerId, name, upstreamModel = name, kind, parameterProfile = "Standard", isEnabled = true,
        residency, publisher, requestBaseUrl, requestAuthMode,
    };

    [Fact]
    public async Task A_Foundry_reranker_shows_its_derived_address_and_its_own_residency()
    {
        using var client = await fixture.ClientAsync();
        var providerId = await ProviderAsync(client);
        var name = "Cohere-rerank-v5-fast-" + Guid.NewGuid().ToString("N")[..6];

        using var create = await client.PostAsJsonAsync("/api/models", Model(providerId, name, residency: "Eu", publisher: "Cohere"), Ct);
        var model = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        model["residency"]!.GetValue<string>().ShouldBe("Eu"); // its own, not the provider's External
        model["residencyOverride"]!.GetValue<string>().ShouldBe("Eu");
        model["publisher"]!.GetValue<string>().ShouldBe("Cohere");
        model["requestBaseUrl"].ShouldBeNull();
        model["rerankBaseUrl"]!.GetValue<string>().ShouldBe("https://umea-res.services.ai.azure.com/providers/cohere/v2");

        // The address set on the model wins; another path on the same resource is fine.
        var id = model["id"]!.GetValue<Guid>();
        using var custom = await client.PutAsJsonAsync($"/api/models/{id}",
            Model(providerId, name, residency: "Eu", requestBaseUrl: "https://umea-res.services.ai.azure.com/providers/cohere/v2/", requestAuthMode: "Bearer"), Ct);
        var updated = await AdminFixture.ReadAsync(custom);
        updated["publisher"]!.GetValue<string>().ShouldBe("Cohere"); // omitted = unchanged
        updated["requestBaseUrl"]!.GetValue<string>().ShouldBe("https://umea-res.services.ai.azure.com/providers/cohere/v2/");
        updated["requestAuthMode"]!.GetValue<string>().ShouldBe("Bearer");
        updated["rerankBaseUrl"]!.GetValue<string>().ShouldBe("https://umea-res.services.ai.azure.com/providers/cohere/v2");

        // Null residency is the provider's again; the change is audited with the new fields.
        using var reset = await client.PutAsJsonAsync($"/api/models/{id}", Model(providerId, name, requestBaseUrl: ""), Ct);
        var back = await AdminFixture.ReadAsync(reset);
        back["residency"]!.GetValue<string>().ShouldBe("External");
        back["residencyOverride"].ShouldBeNull();
        back["requestBaseUrl"].ShouldBeNull();
        back["requestAuthMode"].ShouldBeNull();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var audits = await db.AuditLog.Where(a => a.EntityId == id.ToString()).OrderBy(a => a.Id).Select(a => a.Details).ToListAsync(Ct);
        audits.Count.ShouldBe(3);
        audits[1].ShouldNotBeNull().ShouldContain("Bearer");
        audits[1]!.ShouldContain("providers/cohere/v2/");
    }

    [Theory]
    [InlineData("https://other-res.services.ai.azure.com/providers/cohere/v2")] // another resource would receive the key
    [InlineData("https://umea-res.services.ai.azure.com:8443/providers/cohere/v2")]
    [InlineData("https://umea-res.services.ai.azure.com/providers/cohere/v2?api-key=leak")]
    [InlineData("/providers/cohere/v2")]
    public async Task A_request_address_off_the_providers_resource_is_refused(string requestBaseUrl)
    {
        using var client = await fixture.ClientAsync();
        var providerId = await ProviderAsync(client);
        using var create = await client.PostAsJsonAsync("/api/models", Model(providerId, "rr-" + Guid.NewGuid().ToString("N")[..8], requestBaseUrl: requestBaseUrl), Ct);
        create.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_request_address_is_only_for_a_Foundry_reranker_but_a_residency_is_for_every_model()
    {
        using var client = await fixture.ClientAsync();
        var foundry = await ProviderAsync(client);
        var compatible = await ProviderAsync(client, "OpenAICompatible", "https://umea-res.services.ai.azure.com/v1");
        const string address = "https://umea-res.services.ai.azure.com/providers/cohere/v2";

        using (var chat = await client.PostAsJsonAsync("/api/models", Model(foundry, "chat-" + Guid.NewGuid().ToString("N")[..8], kind: "Chat", requestBaseUrl: address), Ct))
        {
            chat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var other = await client.PostAsJsonAsync("/api/models", Model(compatible, "rr-" + Guid.NewGuid().ToString("N")[..8], requestAuthMode: "Bearer"), Ct))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var onPrem = await client.PostAsJsonAsync("/api/models", Model(compatible, "chat-" + Guid.NewGuid().ToString("N")[..8], kind: "Chat", residency: "OnPrem"), Ct);
        var model = await AdminFixture.ReadAsync(onPrem, HttpStatusCode.Created);
        model["residency"]!.GetValue<string>().ShouldBe("OnPrem");
        model["rerankBaseUrl"].ShouldBeNull();
    }

    [Fact]
    public async Task The_configuration_document_carries_the_new_fields()
    {
        using var client = await fixture.ClientAsync();
        var providerId = await ProviderAsync(client);
        var name = "rr-" + Guid.NewGuid().ToString("N")[..8];
        using (var create = await client.PostAsJsonAsync("/api/models", Model(providerId, name, residency: "Eu", publisher: "Cohere",
            requestBaseUrl: "https://umea-res.openai.azure.com/providers/cohere/v2", requestAuthMode: "Bearer"), Ct))
        {
            await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        }

        using var export = await client.GetAsync("/api/ops/config/export", Ct);
        var document = await AdminFixture.ReadAsync(export);
        var exported = document["models"]!.AsArray().Single(m => m!["configuration"]!["name"]!.GetValue<string>() == name)!["configuration"]!;
        (exported["residency"]!.GetValue<string>(), exported["publisher"]!.GetValue<string>(), exported["requestBaseUrl"]!.GetValue<string>(),
            exported["requestAuthMode"]!.GetValue<string>())
            .ShouldBe(("Eu", "Cohere", "https://umea-res.openai.azure.com/providers/cohere/v2", "Bearer"));

        // Lost in this environment, restored by importing the document.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var stored = await db.ModelDeployments.SingleAsync(m => m.ProviderAccountId == providerId && m.Name == name, Ct);
            (stored.Residency, stored.Publisher, stored.RequestBaseUrl, stored.RequestAuthMode) = (null, null, null, null);
            await db.SaveChangesAsync(Ct);
        }

        var entry = document["models"]!.AsArray().Single(m => m!["configuration"]!["name"]!.GetValue<string>() == name)!;
        var providerName = entry["providerName"]!.GetValue<string>();
        var only = new JsonObject
        {
            ["providers"] = new JsonArray(document["providers"]!.AsArray().Single(p => p!["configuration"]!["name"]!.GetValue<string>() == providerName)!.DeepClone()),
            ["models"] = new JsonArray(entry.DeepClone()),
            ["routes"] = new JsonArray(),
        };
        using var import = await client.PostAsJsonAsync("/api/ops/config/import", only, Ct);
        await AdminFixture.ReadAsync(import);

        using var check = fixture.Services.CreateScope();
        var restored = await check.ServiceProvider.GetRequiredService<GatewayDbContext>().ModelDeployments.AsNoTracking()
            .SingleAsync(m => m.ProviderAccountId == providerId && m.Name == name, Ct);
        (restored.Residency, restored.Publisher, restored.RequestBaseUrl, restored.RequestAuthMode)
            .ShouldBe((DataResidency.Eu, "Cohere", "https://umea-res.openai.azure.com/providers/cohere/v2", ProviderAuthMode.Bearer));
    }
}
