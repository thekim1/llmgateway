using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class ModelListTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string List = """
        {
          "anthropic": {"id":"anthropic","models":{
            "claude-opus-4-5":{"id":"claude-opus-4-5","description":"Flagship Claude model","tool_call":true,"reasoning":true,
              "modalities":{"input":["text","image","pdf"],"output":["text"]},"limit":{"context":200000,"output":64000},
              "cost":{"input":5,"output":25,"cache_read":0.5,"cache_write":6.25}},
            "claude-free":{"cost":{"input":0,"output":0}},
            "claude-no-cost":{"description":"Not priced","limit":{"context":100000}}}},
          "ollama-cloud": {"models":{"gpt-oss:120b":{"cost":{"input":0.1,"output":0.5}}}},
          "openai": {"models":{
            "gpt-5.4":{"cost":{"input":2.5,"output":15,"cache_read":0.25,"tiers":[{"input":5,"output":22.5}],"context_over_200k":{"input":5,"output":22.5}}},
            "gpt-realtime-2.1":{"modalities":{"input":["text","audio"],"output":["text","audio"]},"cost":{"input":4,"output":24,"cache_read":0.4,"input_audio":32,"output_audio":64}},
            "gpt-image-2":{"modalities":{"input":["text","image"],"output":["image"]},"cost":{"input":5,"output":40}},
            "tiny":{"cost":{"input":0.0000001234,"output":1}}}},
          "someone-else": {"models":{"claude-opus-4-5":{"cost":{"input":99,"output":99}}}}
        }
        """;

    private static ModelList Parse(string json = List)
    {
        using var document = JsonDocument.Parse(json);
        return ModelList.Parse(document);
    }

    private static ProviderAccount Provider(ProviderType type, string baseUrl) => new() { Name = "p", Type = type, BaseUrl = baseUrl };

    [Fact]
    public void List_keeps_the_mapped_providers_with_prices_and_details()
    {
        var list = Parse();
        var opus = list.Find("anthropic", "claude-opus-4-5").ShouldNotBeNull();
        opus.Key.ShouldBe("anthropic/claude-opus-4-5");
        opus.Price.ShouldBe(new ListPrice(5, 25, 0.5m, 6.25m));
        opus.Description.ShouldBe("Flagship Claude model");
        opus.ContextWindow.ShouldBe(200000);
        opus.Features.ShouldBe(["thinking", "tools", "vision"]);
        list.Find("ANTHROPIC", "Claude-Opus-4-5").ShouldNotBeNull();
        // 0/0 is taken as unknown, never as free; a model without a cost is listed for its details only.
        list.Find("anthropic", "claude-free").ShouldNotBeNull().Price.ShouldBeNull();
        var unpriced = list.Find("anthropic", "claude-no-cost").ShouldNotBeNull();
        (unpriced.Price, unpriced.Description, unpriced.ContextWindow).ShouldBe((null, "Not priced", 100000));
        list.Find("openai", "gpt-5.4")!.Price.ShouldBe(new ListPrice(2.5m, 15, 0.25m)); // context tiers are not used
        list.Find("openai", "gpt-realtime-2.1")!.Price.ShouldBe(new ListPrice(4, 24, 0.4m, null, 32, 64));
        list.Find("openai", "gpt-realtime-2.1")!.Features.ShouldBe(["audio"]);
        list.Find("openai", "gpt-image-2")!.Features.ShouldBe(["image generation", "vision"]);
        list.Find("openai", "tiny")!.Price!.Input.ShouldBe(0.000000m); // six decimals, as stored
        list.Find("someone-else", "claude-opus-4-5").ShouldBeNull();
        list.Count.ShouldBe(8);
    }

    [Theory]
    [InlineData("gpt-oss:120b")]
    [InlineData("gpt-oss:120b-cloud")]
    [InlineData(" gpt-oss:120b ")]
    public void Ollama_names_are_found_with_or_without_their_suffix(string model) =>
        Parse().Find("ollama-cloud", model)!.Key.ShouldBe("ollama-cloud/gpt-oss:120b");

    [Theory]
    [InlineData(ProviderType.Anthropic, "https://api.anthropic.com/v1", "anthropic")]
    [InlineData(ProviderType.Anthropic, "https://res.services.ai.azure.com/anthropic/v1", "azure")]
    [InlineData(ProviderType.OllamaCloud, "https://ollama.com/v1", "ollama-cloud")]
    [InlineData(ProviderType.Ollama, "https://ollama.com/v1", "ollama-cloud")]
    [InlineData(ProviderType.Ollama, "http://ollama.local:11434/v1", null)]
    [InlineData(ProviderType.OpenAI, "https://api.openai.com/v1", "openai")]
    [InlineData(ProviderType.OpenAICompatible, "https://openrouter.ai/api/v1", "openrouter")]
    [InlineData(ProviderType.OpenAICompatible, "https://vllm.internal/v1", null)]
    [InlineData(ProviderType.AzureAIFoundry, "https://res.services.ai.azure.com/api/projects/p", "azure")]
    [InlineData(ProviderType.AzureOpenAI, "https://res.openai.azure.com/openai/v1", "azure")]
    public void Providers_map_to_the_list_provider_whose_prices_they_pay(ProviderType type, string baseUrl, string? expected) =>
        ModelList.ProviderId(Provider(type, baseUrl)).ShouldBe(expected);

    [Theory]
    [InlineData("claude-opus-4-5", true, true, true, PriceMode.List)]
    [InlineData("claude-opus-4-5", false, true, true, PriceMode.Manual)]
    [InlineData("claude-opus-4-5", true, false, true, PriceMode.Manual)]
    [InlineData("claude-opus-4-5", true, true, false, PriceMode.Manual)]
    [InlineData("claude-no-cost", true, true, true, PriceMode.Unlisted)]
    [InlineData("claude-elsewhere", true, true, true, PriceMode.Unlisted)]
    public void Model_follows_the_list_only_when_it_its_provider_and_the_update_are_on_and_the_list_prices_it(
        string upstream, bool model, bool provider, bool updatesOn, PriceMode expected)
    {
        var account = Provider(ProviderType.Anthropic, "https://api.anthropic.com/v1");
        account.AutoUpdatePrices = provider;
        var deployment = new ModelDeployment { Name = upstream, UpstreamModel = upstream, AutoUpdatePrice = model, ProviderAccount = account };
        Parse().Mode(deployment, updatesOn).ShouldBe(expected);
    }

    [Theory]
    [InlineData("gpt-5.4", null, PriceMode.Manual)] // Azure's price depends on region and deployment type
    [InlineData("prod-claude", "claude-opus-4-5", PriceMode.List)] // Claude is billed at Anthropic's price
    public void Azure_deployments_keep_their_price_except_Claude(string upstream, string? baseModel, PriceMode expected)
    {
        var list = Parse("""
            {"azure":{"models":{"gpt-5.4":{"cost":{"input":2.5,"output":15}},"claude-opus-4-5":{"cost":{"input":5,"output":25}}}}}
            """);
        var foundry = Provider(ProviderType.AzureAIFoundry, "https://res.services.ai.azure.com/api/projects/p");
        var model = new ModelDeployment { Name = upstream, UpstreamModel = upstream, BaseModel = baseModel, ProviderAccount = foundry };
        list.Mode(model, updatesOn: true).ShouldBe(expected);
        list.Find(model)!.Key.ShouldBe($"azure/{baseModel ?? upstream}");
        // Without any price, an Azure deployment gets the list price once; with one, it keeps it.
        list.FillsMissingPrice(model, updatesOn: true).ShouldBe(expected == PriceMode.Manual);
        model.Prices.Add(new ModelPrice { InputPerMillionUsd = 1, OutputPerMillionUsd = 1 });
        list.FillsMissingPrice(model, updatesOn: true).ShouldBeFalse();
    }

    [Fact]
    public void A_new_list_price_keeps_what_the_list_does_not_give()
    {
        var current = new ModelPrice
        {
            InputPerMillionUsd = 4, CachedInputPerMillionUsd = 4, OutputPerMillionUsd = 20, CacheWriteInputPerMillionUsd = 5,
            AudioInputPerMillionUsd = 30, AudioOutputPerMillionUsd = 60, AudioPerMinuteUsd = 0.006m, PerImageUsd = 0.04m,
        };
        var next = ModelList.Changed(current, new ListPrice(5, 25)).ShouldNotBeNull();
        (next.InputPerMillionUsd, next.OutputPerMillionUsd).ShouldBe((5m, 25m));
        next.CachedInputPerMillionUsd.ShouldBe(5); // cached cost the input price, so it still does
        next.CacheWriteInputPerMillionUsd.ShouldBe(5);
        (next.AudioInputPerMillionUsd, next.AudioOutputPerMillionUsd, next.AudioPerMinuteUsd, next.PerImageUsd).ShouldBe((30m, 60m, 0.006m, 0.04m));

        var discounted = new ModelPrice { InputPerMillionUsd = 4, CachedInputPerMillionUsd = 1, OutputPerMillionUsd = 20 };
        ModelList.Changed(discounted, new ListPrice(5, 25))!.CachedInputPerMillionUsd.ShouldBe(1);

        var fresh = ModelList.Changed(null, new ListPrice(5, 25, 0.5m, 6.25m)).ShouldNotBeNull();
        (fresh.CachedInputPerMillionUsd, fresh.CacheWriteInputPerMillionUsd, fresh.AudioInputPerMillionUsd).ShouldBe((0.5m, (decimal?)6.25m, 0m));
    }

    [Fact]
    public void An_unchanged_list_price_adds_nothing()
    {
        var current = new ModelPrice { InputPerMillionUsd = 5, CachedInputPerMillionUsd = 0.5m, OutputPerMillionUsd = 25, CacheWriteInputPerMillionUsd = 6.25m, PerImageUsd = 1 };
        ModelList.Changed(current, new ListPrice(5, 25, 0.5m, 6.25m)).ShouldBeNull();
        ModelList.Changed(current, new ListPrice(5, 25, 0.5m, 7m)).ShouldNotBeNull();
    }

    [Fact]
    public void Details_fill_only_what_is_empty()
    {
        var listed = Parse().Find("anthropic", "claude-opus-4-5")!;
        var empty = new ModelDeployment { Name = "m", UpstreamModel = "m" };
        ModelList.Enrich(empty, listed).ShouldBeTrue();
        (empty.Description, empty.ContextWindow).ShouldBe(("Flagship Claude model", (int?)200000));
        empty.Features.ShouldBe(["thinking", "tools", "vision"]);
        ModelList.Enrich(empty, listed).ShouldBeFalse();

        var own = new ModelDeployment { Name = "m", UpstreamModel = "m", Description = "Ours", ContextWindow = 1000, Features = ["code"] };
        ModelList.Enrich(own, listed).ShouldBeFalse();
        (own.Description, own.ContextWindow, own.Features.Single()).ShouldBe(("Ours", (int?)1000, "code"));
    }

    [Fact]
    public async Task List_is_downloaded_once_a_day()
    {
        var calls = 0;
        using var handler = new FakeList(request =>
        {
            calls++;
            request.RequestUri!.ToString().ShouldBe("https://models.dev/api.json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(List, Encoding.UTF8, "application/json") };
        });
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var service = Service(handler, time);

        (await service.GetAsync(Ct)).Count.ShouldBe(8);
        await service.GetAsync(Ct);
        calls.ShouldBe(1);
        time.Advance(TimeSpan.FromHours(25));
        await service.GetAsync(Ct);
        calls.ShouldBe(2);
        await service.GetAsync(Ct, reload: true);
        calls.ShouldBe(3);
        service.LastError.ShouldBeNull();
        service.LoadedAt.ShouldBe(time.GetUtcNow());
    }

    [Theory]
    [InlineData(503, "{}", "The price list answered 503.")]
    [InlineData(200, "not json", "The price list could not be read.")]
    [InlineData(200, "[]", "The price list could not be read.")]
    public async Task Failed_download_keeps_the_last_list(int status, string body, string error)
    {
        var fail = false;
        using var handler = new FakeList(_ => fail
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(List) });
        using var service = Service(handler);
        await service.GetAsync(Ct);
        fail = true;
        (await service.GetAsync(Ct, reload: true)).Count.ShouldBe(8);
        service.LastError.ShouldBe(error);
    }

    [Fact]
    public async Task Empty_address_turns_the_list_off()
    {
        using var handler = new FakeList(_ => throw new InvalidOperationException("not called"));
        using var service = Service(handler, options: new AdminOptions { PriceListUrl = "" });
        (service.Enabled, service.UpdatesPrices).ShouldBe((false, false));
        (await service.GetAsync(Ct)).Count.ShouldBe(0);
        using var noUpdates = Service(handler, options: new AdminOptions { AutoUpdatePrices = false });
        (noUpdates.Enabled, noUpdates.UpdatesPrices).ShouldBe((true, false));
    }

    private static ModelListService Service(HttpMessageHandler handler, TimeProvider? time = null, AdminOptions? options = null) =>
        new(new FakeClientFactory(handler), Options.Create(options ?? new AdminOptions()), time ?? TimeProvider.System, NullLogger<ModelListService>.Instance);

    private sealed class FakeList(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.ShouldBe(ModelListService.HttpClientName);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}

/// <summary>The admin API's own list and update, with <see cref="AdminFixture.ModelListJson"/> as the models.dev list.</summary>
public sealed class ModelListSyncTests(AdminFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Price(decimal input, decimal output) => new { inputPerMillionUsd = input, cachedInputPerMillionUsd = input, outputPerMillionUsd = output };

    [Fact]
    public async Task Listed_models_follow_the_list_and_only_others_take_a_price_by_hand()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        string listed = "claude-a-" + suffix, optedOut = "claude-b-" + suffix, unlisted = "claude-c-" + suffix, offProvider = "claude-d-" + suffix;
        AdminFixture.ModelListJson = """
            {"anthropic":{"models":{
              "LISTED":{"description":"A listed model","tool_call":true,"limit":{"context":200000},"cost":{"input":5,"output":25,"cache_read":0.5,"cache_write":6.25}},
              "OPTED":{"cost":{"input":5,"output":25}},
              "OFF":{"cost":{"input":5,"output":25}}}}}
            """.Replace("LISTED", listed, StringComparison.Ordinal).Replace("OPTED", optedOut, StringComparison.Ordinal).Replace("OFF", offProvider, StringComparison.Ordinal);
        var sync = fixture.Services.GetRequiredService<ModelListSync>();
        await fixture.Services.GetRequiredService<ModelListService>().GetAsync(Ct, reload: true);

        var provider = await ProviderAsync(client, "anthropic-" + suffix, autoUpdatePrices: true);
        var manualProvider = await ProviderAsync(client, "anthropic-manual-" + suffix, autoUpdatePrices: false);
        var listedId = await ModelAsync(client, provider, listed, Price(1, 2));
        var optedOutId = await ModelAsync(client, provider, optedOut, null, autoUpdatePrice: false);
        var unlistedId = await ModelAsync(client, provider, unlisted, null);
        var offProviderId = await ModelAsync(client, manualProvider, offProvider, null);

        var models = await ModelsAsync(client);
        (models[listedId]["priceMode"]!.GetValue<string>(), models[listedId]["priceListKey"]!.GetValue<string>()).ShouldBe(("List", $"anthropic/{listed}"));
        models[optedOutId]["priceMode"]!.GetValue<string>().ShouldBe("Manual");
        models[unlistedId]["priceMode"]!.GetValue<string>().ShouldBe("Unlisted");
        models[offProviderId]["priceMode"]!.GetValue<string>().ShouldBe("Manual");

        // A model that follows the list takes no price by hand; the others do.
        using (var refused = await client.PostAsJsonAsync($"/api/models/{listedId}/prices", Price(3, 4), Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        foreach (var id in new[] { optedOutId, unlistedId, offProviderId })
        {
            using var added = await client.PostAsJsonAsync($"/api/models/{id}/prices", Price(3, 4), Ct);
            added.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await sync.RunOnceAsync(Ct); // another run (the hosted one) may have done it first; the result is the same
        sync.Status().Error.ShouldBeNull();

        var prices = await PricesAsync(listedId);
        prices.Count.ShouldBe(2);
        var latest = prices[^1];
        (latest.InputPerMillionUsd, latest.CachedInputPerMillionUsd, latest.OutputPerMillionUsd, latest.CacheWriteInputPerMillionUsd)
            .ShouldBe((5m, 0.5m, 25m, (decimal?)6.25m));
        latest.Source.ShouldBe($"models.dev anthropic/{listed}");
        (await PricesAsync(optedOutId)).Single().InputPerMillionUsd.ShouldBe(3);
        (await PricesAsync(offProviderId)).Single().InputPerMillionUsd.ShouldBe(3);
        (await PricesAsync(unlistedId)).Single().Source.ShouldBeNull();

        // Empty details are filled from the list.
        models = await ModelsAsync(client);
        models[listedId]["description"]!.GetValue<string>().ShouldBe("A listed model");
        models[listedId]["contextWindow"]!.GetValue<int>().ShouldBe(200000);
        models[listedId]["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldBe(["tools"]);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var audit = await db.AuditLog.Where(a => a.EntityId == listedId.ToString() && a.Actor == ModelListSync.Actor).ToListAsync(Ct);
            audit.Select(a => a.Action).Order().ShouldBe(["price", "update"]);
        }

        // The same list again adds nothing; turning automatic prices back on lets the list take over.
        await sync.RunOnceAsync(Ct);
        (await PricesAsync(listedId)).Count.ShouldBe(2);
        using (var on = await client.PutAsJsonAsync($"/api/models/{optedOutId}", new
        {
            name = optedOut, upstreamModel = optedOut, kind = "Chat", parameterProfile = "Standard", isEnabled = true, autoUpdatePrice = true,
        }, Ct))
        {
            var model = await AdminFixture.ReadAsync(on);
            (model["autoUpdatePrice"]!.GetValue<bool>(), model["priceMode"]!.GetValue<string>()).ShouldBe((true, "List"));
        }
        await sync.RunOnceAsync(Ct);
        (await PricesAsync(optedOutId))[^1].InputPerMillionUsd.ShouldBe(5);
        (await PricesAsync(offProviderId)).Single().InputPerMillionUsd.ShouldBe(3); // the provider still has it off
    }

    [Fact]
    public async Task Azure_deployments_get_a_missing_price_once_and_keep_their_own()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var baseModel = "gpt-test-" + suffix;
        AdminFixture.ModelListJson = """
            {"azure":{"models":{"BASE":{"description":"Azure model","cost":{"input":2,"output":8}}}}}
            """.Replace("BASE", baseModel, StringComparison.Ordinal);
        await fixture.Services.GetRequiredService<ModelListService>().GetAsync(Ct, reload: true);
        using var create = await client.PostAsJsonAsync("/api/providers", new
        {
            name = "foundry-" + suffix, baseUrl = "https://res.services.ai.azure.com/api/projects/p", type = "AzureAIFoundry", authMode = "ApiKeyHeader",
            credential = "secret", residency = "Eu", capabilities = new[] { "ChatCompletions", "Streaming" }, timeoutSeconds = 10, isEnabled = true,
        }, Ct);
        var provider = (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
        async Task<Guid> DeploymentAsync(string name, object? price)
        {
            using var model = await client.PostAsJsonAsync("/api/models", new
            {
                providerId = provider, name, upstreamModel = name, baseModel, kind = "Chat", parameterProfile = "Standard", isEnabled = true, price,
            }, Ct);
            var created = await AdminFixture.ReadAsync(model, HttpStatusCode.Created);
            (created["priceMode"]!.GetValue<string>(), created["priceListKey"]!.GetValue<string>()).ShouldBe(("Manual", $"azure/{baseModel}"));
            return created["id"]!.GetValue<Guid>();
        }
        var unpriced = await DeploymentAsync("prod-a-" + suffix, null);
        var priced = await DeploymentAsync("prod-b-" + suffix, Price(3, 9));

        await fixture.Services.GetRequiredService<ModelListSync>().RunOnceAsync(Ct);

        var filled = (await PricesAsync(unpriced)).Single();
        (filled.InputPerMillionUsd, filled.OutputPerMillionUsd, filled.Source).ShouldBe((2m, 8m, $"models.dev azure/{baseModel}"));
        (await PricesAsync(priced)).Single().InputPerMillionUsd.ShouldBe(3);
        (await ModelsAsync(client))[priced]["description"]!.GetValue<string>().ShouldBe("Azure model");
        // An Azure deployment takes a price by hand.
        using var added = await client.PostAsJsonAsync($"/api/models/{unpriced}/prices", Price(4, 10), Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Description_is_kept_cleared_and_shown_in_the_catalogue()
    {
        using var client = await fixture.ClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var provider = await ProviderAsync(client, "anthropic-desc-" + suffix, autoUpdatePrices: true);
        var name = "described-" + suffix;
        using (var create = await client.PostAsJsonAsync("/api/models", new
        {
            providerId = provider, name, upstreamModel = name, kind = "Chat", parameterProfile = "Standard", isEnabled = true,
            description = "Good at Swedish", features = new[] { "tools" },
        }, Ct))
        {
            (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["description"]!.GetValue<string>().ShouldBe("Good at Swedish");
        }
        var id = (await ModelsAsync(client)).Single(m => m.Value["name"]!.GetValue<string>() == name).Key;

        using (var catalog = await client.GetAsync("/api/catalog", Ct))
        {
            var entry = (await AdminFixture.ReadAsync(catalog))["models"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!;
            entry["description"]!.GetValue<string>().ShouldBe("Good at Swedish");
            entry["features"]!.AsArray().Single()!.GetValue<string>().ShouldBe("tools");
        }

        var body = new { name, upstreamModel = name, kind = "Chat", parameterProfile = "Standard", isEnabled = true };
        using (var kept = await client.PutAsJsonAsync($"/api/models/{id}", body, Ct))
        {
            (await AdminFixture.ReadAsync(kept))["description"]!.GetValue<string>().ShouldBe("Good at Swedish");
        }
        using (var cleared = await client.PutAsJsonAsync($"/api/models/{id}", new { body.name, body.upstreamModel, body.kind, body.parameterProfile, body.isEnabled, description = "" }, Ct))
        {
            (await AdminFixture.ReadAsync(cleared))["description"].ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("viewer", 403)]
    [InlineData("department-admin", 403)]
    public async Task Only_gateway_admins_see_or_run_the_update(string? role, int status)
    {
        using var client = await fixture.ClientAsync(role);
        using var read = await client.GetAsync("/api/settings/model-prices", Ct);
        ((int)read.StatusCode).ShouldBe(status);
        using var run = await client.PostAsync("/api/settings/model-prices/run", null, Ct);
        ((int)run.StatusCode).ShouldBe(status);
    }

    [Fact]
    public async Task Status_shows_the_update_is_on()
    {
        using var client = await fixture.ClientAsync();
        using var read = await client.GetAsync("/api/settings/model-prices", Ct);
        var status = await AdminFixture.ReadAsync(read);
        status["enabled"]!.GetValue<bool>().ShouldBeTrue();
        status["priceListUrl"]!.GetValue<string>().ShouldBe("https://models.dev.test/api.json");
    }

    private static async Task<Guid> ProviderAsync(HttpClient client, string name, bool autoUpdatePrices)
    {
        using var create = await client.PostAsJsonAsync("/api/providers", new
        {
            name, baseUrl = "https://api.anthropic.com/v1", type = "Anthropic", authMode = "XApiKeyHeader", credential = "secret",
            residency = "Eu", capabilities = new[] { "ChatCompletions", "Streaming" }, timeoutSeconds = 10, isEnabled = true, autoUpdatePrices,
        }, Ct);
        var provider = await AdminFixture.ReadAsync(create, HttpStatusCode.Created);
        (provider["autoUpdatePrices"]!.GetValue<bool>(), provider["priceList"]!.GetValue<string>()).ShouldBe((autoUpdatePrices, "anthropic"));
        return provider["id"]!.GetValue<Guid>();
    }

    private static async Task<Guid> ModelAsync(HttpClient client, Guid provider, string name, object? price, bool? autoUpdatePrice = null)
    {
        using var create = await client.PostAsJsonAsync("/api/models", new
        {
            providerId = provider, name, upstreamModel = name, kind = "Chat", parameterProfile = "Standard", isEnabled = true, price, autoUpdatePrice,
        }, Ct);
        return (await AdminFixture.ReadAsync(create, HttpStatusCode.Created))["id"]!.GetValue<Guid>();
    }

    private static async Task<Dictionary<Guid, JsonNode>> ModelsAsync(HttpClient client)
    {
        using var list = await client.GetAsync("/api/models", Ct);
        return (await AdminFixture.ReadAsync(list)).AsArray().ToDictionary(m => m!["id"]!.GetValue<Guid>(), m => m!);
    }

    private async Task<List<ModelPrice>> PricesAsync(Guid model)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        return await db.ModelPrices.AsNoTracking().Where(p => p.ModelDeploymentId == model).OrderBy(p => p.EffectiveFrom).ToListAsync(Ct);
    }
}
