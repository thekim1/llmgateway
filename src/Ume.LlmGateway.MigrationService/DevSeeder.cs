using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.MigrationService;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Seed demo organisation, providers, models and routes when the database is empty.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL (incl. /v1) of the fake OpenAI/Anthropic-compatible provider used for demos and tests.</summary>
    public string? FakeLlmUrl { get; set; }

    /// <summary>Base URL (incl. /v1) of a local Ollama server.</summary>
    public string? OllamaUrl { get; set; }

    public string OllamaModel { get; set; } = "qwen2.5:0.5b";

    /// <summary>Optional development key: either a full <c>ume-sk-…</c> key or its 43-character random part.</summary>
    public string? DevKey { get; set; }

    public string? AzureOpenAIEndpoint { get; set; }
    public string? AIFoundryEndpoint { get; set; }

    /// <summary>Provider credentials keyed by provider name (openai, azure-openai-swc, ai-foundry, anthropic, ollama-cloud).</summary>
    public Dictionary<string, string> Credentials { get; set; } = [];
}

/// <summary>
/// Development/demo seed. Creates a realistic but fictional set of förvaltningar, teams, providers, models,
/// prices and routes. Real providers are created disabled unless a credential is configured.
/// </summary>
public sealed partial class DevSeeder(
    IOptions<SeedOptions> options,
    CredentialProtector protector,
    VirtualKeyHasher hasher,
    TimeProvider time,
    ILogger<DevSeeder> logger)
{
    public async Task SeedAsync(GatewayDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        if (await db.Departments.AnyAsync(cancellationToken))
        {
            await SyncManagedProvidersAsync(db, o, cancellationToken);
            await EnsureDevKeyAsync(db, o, cancellationToken);
            return;
        }

        var now = time.GetUtcNow();
        var priceDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // --- Organisation -------------------------------------------------------------------
        var klk = new Department { Name = "Kommunledningskontoret", CostCenterCode = "1000", CreatedAt = now };
        var soc = new Department { Name = "Individ- och familjeomsorgen", CostCenterCode = "3000", CreatedAt = now };
        var fritid = new Department { Name = "Fritidsförvaltningen", CostCenterCode = "5000", CreatedAt = now };
        var aiTeam = new Team { Department = klk, Name = "Digitalisering & AI", Description = "Utforskar och förvaltar AI-tjänster", CreatedAt = now };
        var contact = new Team { Department = klk, Name = "Kontaktcenter", Description = "Svarar på frågor från invånare", CreatedAt = now };
        var socDev = new Team { Department = soc, Name = "Verksamhetsutveckling", CreatedAt = now };
        var bookings = new Team { Department = fritid, Name = "Bokning & anläggningar", CreatedAt = now };
        db.AddRange(klk, soc, fritid, aiTeam, contact, socDev, bookings);

        // --- Providers ------------------------------------------------------------------------
        const ProviderCapabilities chatAll = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming;
        var providers = new List<ProviderAccount>();
        ProviderAccount Provider(string name, string display, ProviderType type, string url, ProviderAuthMode auth, DataResidency residency, ProviderCapabilities caps, bool enabled)
        {
            var credential = o.Credentials.GetValueOrDefault(name);
            var p = new ProviderAccount
            {
                Name = name,
                DisplayName = display,
                Type = type,
                BaseUrl = url,
                AuthMode = auth,
                Residency = residency,
                Capabilities = caps,
                IsEnabled = enabled,
                EncryptedCredential = protector.Protect(credential),
                CreatedAt = now,
            };
            providers.Add(p);
            return p;
        }

        var fakeUrl = o.FakeLlmUrl ?? "http://localhost:5090/v1";
        var hasFake = !string.IsNullOrWhiteSpace(o.FakeLlmUrl);
        var fakeOnPrem = Provider("fake-onprem", "Testleverantör (on-prem)", ProviderType.OpenAICompatible, fakeUrl, ProviderAuthMode.None, DataResidency.OnPrem,
            chatAll | ProviderCapabilities.Embeddings | ProviderCapabilities.Responses, hasFake);
        var fakeEu = Provider("fake-eu", "Testleverantör (EU)", ProviderType.OpenAICompatible, fakeUrl, ProviderAuthMode.None, DataResidency.Eu,
            chatAll | ProviderCapabilities.Embeddings | ProviderCapabilities.Responses, hasFake);
        var fakeExternal = Provider("fake-external", "Testleverantör (extern)", ProviderType.OpenAICompatible, fakeUrl, ProviderAuthMode.None, DataResidency.External,
            chatAll | ProviderCapabilities.Responses, hasFake);
        var fakeAnthropic = Provider("fake-anthropic", "Testleverantör (Anthropic-format)", ProviderType.Anthropic, fakeUrl, ProviderAuthMode.XApiKeyHeader, DataResidency.External,
            chatAll | ProviderCapabilities.AnthropicMessages, hasFake);

        var ollamaUrl = o.OllamaUrl;
        var ollama = Provider("ollama-local", "Ollama (lokal server)", ProviderType.Ollama, ollamaUrl ?? "http://localhost:11434/v1", ProviderAuthMode.None, DataResidency.OnPrem,
            chatAll | ProviderCapabilities.Embeddings, !string.IsNullOrWhiteSpace(ollamaUrl));

        bool HasCredential(string name) => !string.IsNullOrWhiteSpace(o.Credentials.GetValueOrDefault(name));
        var openai = Provider("openai", "OpenAI", ProviderType.OpenAI, "https://api.openai.com/v1", ProviderAuthMode.Bearer, DataResidency.External,
            chatAll | ProviderCapabilities.Embeddings | ProviderCapabilities.Responses, HasCredential("openai"));
        var azure = Provider("azure-openai-swc", "Azure OpenAI (Sweden Central)", ProviderType.AzureOpenAI,
            o.AzureOpenAIEndpoint is { Length: > 0 } az ? az.TrimEnd('/') + "/openai/v1" : "https://example-swc.openai.azure.com/openai/v1",
            ProviderAuthMode.ApiKeyHeader, DataResidency.Eu, chatAll | ProviderCapabilities.Embeddings | ProviderCapabilities.Responses,
            HasCredential("azure-openai-swc") && !string.IsNullOrWhiteSpace(o.AzureOpenAIEndpoint));
        var foundry = Provider("ai-foundry", "Azure AI Foundry (Sweden Central)", ProviderType.AzureAIFoundry,
            o.AIFoundryEndpoint is { Length: > 0 } af ? af.TrimEnd('/') + "/openai/v1" : "https://example-swc.services.ai.azure.com/openai/v1",
            ProviderAuthMode.ApiKeyHeader, DataResidency.Eu, chatAll | ProviderCapabilities.Embeddings,
            HasCredential("ai-foundry") && !string.IsNullOrWhiteSpace(o.AIFoundryEndpoint));
        var anthropic = Provider("anthropic", "Anthropic", ProviderType.Anthropic, "https://api.anthropic.com/v1", ProviderAuthMode.XApiKeyHeader, DataResidency.External,
            chatAll | ProviderCapabilities.AnthropicMessages, HasCredential("anthropic"));
        var ollamaCloud = Provider("ollama-cloud", "Ollama Cloud", ProviderType.OllamaCloud, "https://ollama.com/v1", ProviderAuthMode.Bearer, DataResidency.External,
            chatAll, HasCredential("ollama-cloud"));
        db.AddRange(providers);

        // --- Models & prices (USD per 1M tokens; example list prices – verify before production use) ----
        ModelDeployment Model(ProviderAccount provider, string name, string upstream, decimal input, decimal cached, decimal output,
            ModelKind kind = ModelKind.Chat, ParameterProfile profile = ParameterProfile.Standard, int? context = null)
        {
            var m = new ModelDeployment
            {
                ProviderAccount = provider,
                Name = $"{provider.Name}/{name}",
                UpstreamModel = upstream,
                Kind = kind,
                ParameterProfile = profile,
                ContextWindow = context,
                Prices = [new ModelPrice { EffectiveFrom = priceDate, InputPerMillionUsd = input, CachedInputPerMillionUsd = cached, OutputPerMillionUsd = output }],
            };
            db.Add(m);
            return m;
        }

        var fakeSmall = Model(fakeOnPrem, "echo-small", "fake-chat", 0.10m, 0.10m, 0.30m, context: 32_000);
        var fakeEmbed = Model(fakeOnPrem, "echo-embed", "fake-embed", 0.02m, 0m, 0m, ModelKind.Embedding);
        var fakeEuModel = Model(fakeEu, "echo-eu", "fake-chat", 0.40m, 0.10m, 1.60m, context: 128_000);
        var fakeExt = Model(fakeExternal, "echo-external", "fake-chat", 2.50m, 1.25m, 10.00m, context: 128_000);
        var fakeFail = Model(fakeExternal, "always-503", "fake-fail-503", 2.50m, 1.25m, 10.00m);
        var fakeClaude = Model(fakeAnthropic, "claude-fake", "fake-claude", 3.00m, 0.30m, 15.00m, context: 200_000);
        var ollamaModel = Model(ollama, o.OllamaModel.Replace(':', '-'), o.OllamaModel, 0m, 0m, 0m, context: 32_000);
        var gpt41mini = Model(openai, "gpt-4.1-mini", "gpt-4.1-mini", 0.40m, 0.10m, 1.60m, context: 1_047_576);
        var gpt5mini = Model(openai, "gpt-5-mini", "gpt-5-mini", 0.25m, 0.025m, 2.00m, profile: ParameterProfile.OpenAIReasoning, context: 400_000);
        var embedSmall = Model(openai, "text-embedding-3-small", "text-embedding-3-small", 0.02m, 0m, 0m, ModelKind.Embedding);
        var azureMini = Model(azure, "gpt-4.1-mini", "gpt-4.1-mini", 0.40m, 0.10m, 1.60m, context: 1_047_576);
        Model(foundry, "gpt-4.1-mini", "gpt-4.1-mini", 0.40m, 0.10m, 1.60m, context: 1_047_576);
        var sonnet = Model(anthropic, "claude-sonnet-4-5", "claude-sonnet-4-5", 3.00m, 0.30m, 15.00m, context: 200_000);
        Model(anthropic, "claude-haiku-4-5", "claude-haiku-4-5", 1.00m, 0.10m, 5.00m, context: 200_000);
        Model(ollamaCloud, "gpt-oss-120b", "gpt-oss:120b", 0m, 0m, 0m, context: 128_000);

        // --- Routes (aliases with fallback chains); disabled providers are skipped at runtime -----
        static RouteTarget T(ModelDeployment m, int priority, int weight = 1) => new() { ModelDeployment = m, Priority = priority, Weight = weight };
        db.AddRange(
            new RouteAlias
            {
                Name = "ume/chat-standard",
                Description = "Allmän chattmodell. Körs i EU i första hand, med reserv on-prem.",
                Targets = [T(azureMini, 0), T(fakeEuModel, 1), T(ollamaModel, 2), T(fakeSmall, 3)],
            },
            new RouteAlias
            {
                Name = "ume/chat-onprem",
                Description = "Endast kommunens egna servrar. Lämplig för känsliga uppgifter.",
                Targets = [T(ollamaModel, 0), T(fakeSmall, 1)],
            },
            new RouteAlias
            {
                Name = "ume/chat-advanced",
                Description = "Mest kapabla modeller (extern leverantör). Ej för personuppgifter.",
                Targets = [T(sonnet, 0, 1), T(gpt5mini, 0, 1), T(fakeClaude, 1), T(fakeExt, 2)],
            },
            new RouteAlias
            {
                Name = "ume/demo-fallback",
                Description = "Demo: första leverantören svarar alltid 503 så att reservleverantören används.",
                Targets = [T(fakeFail, 0), T(fakeEuModel, 1)],
            },
            new RouteAlias
            {
                Name = "ume/embeddings",
                Kind = ModelKind.Embedding,
                Description = "Vektorer för sökning (RAG).",
                Targets = [T(embedSmall, 0), T(fakeEmbed, 1)],
            });

        db.Add(new ExchangeRate { Currency = "USD", SekPerUnit = 9.50m, EffectiveFrom = priceDate });

        db.AddRange(
            new Budget { Scope = BudgetScope.Department, ScopeId = klk.Id, LimitSek = 10_000m, Period = BudgetPeriod.Monthly, CreatedAt = now },
            new Budget { Scope = BudgetScope.Department, ScopeId = soc.Id, LimitSek = 5_000m, Period = BudgetPeriod.Monthly, CreatedAt = now },
            new Budget { Scope = BudgetScope.Team, ScopeId = aiTeam.Id, LimitSek = 2_000m, Period = BudgetPeriod.Monthly, CreatedAt = now },
            new Budget { Scope = BudgetScope.Team, ScopeId = contact.Id, LimitSek = 300m, Period = BudgetPeriod.Daily, AlertThresholds = [80, 100], CreatedAt = now });

        db.AuditLog.Add(new AuditLogEntry { Timestamp = now, Actor = "system:seed", Action = "seed", EntityType = "Database", Details = "Demo data created" });
        await db.SaveChangesAsync(cancellationToken);
        LogSeeded(logger, providers.Count);

        await EnsureDevKeyAsync(db, o, cancellationToken);
    }

    /// <summary>Keeps URLs of dev-managed providers in sync with the AppHost (ports can change between runs).</summary>
    private static async Task SyncManagedProvidersAsync(GatewayDbContext db, SeedOptions o, CancellationToken cancellationToken)
    {
        var managed = await db.ProviderAccounts
            .Where(p => p.Name.StartsWith("fake-") || p.Name == "ollama-local")
            .ToListAsync(cancellationToken);
        foreach (var p in managed)
        {
            var url = p.Name == "ollama-local" ? o.OllamaUrl : o.FakeLlmUrl;
            if (!string.IsNullOrWhiteSpace(url) && p.BaseUrl != url)
            {
                p.BaseUrl = url;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDevKeyAsync(GatewayDbContext db, SeedOptions o, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(o.DevKey))
        {
            return;
        }

        var plain = o.DevKey.StartsWith(VirtualKeyHasher.KeyPrefix, StringComparison.Ordinal) ? o.DevKey : VirtualKeyHasher.KeyPrefix + o.DevKey;
        if (!VirtualKeyHasher.LooksLikeKey(plain))
        {
            LogInvalidDevKey(logger);
            return;
        }

        var hash = hasher.Hash(plain);
        if (await db.VirtualKeys.AnyAsync(k => k.KeyHash == hash, cancellationToken))
        {
            return;
        }

        var team = await db.Teams.OrderBy(t => t.Name).FirstAsync(t => t.Name == "Digitalisering & AI", cancellationToken);
        var key = new VirtualKey
        {
            TeamId = team.Id,
            Name = "Utvecklingsnyckel (lokal miljö)",
            Description = "Skapad automatiskt för lokal utveckling och tester.",
            Prefix = plain[..11],
            KeyHash = hash,
            EncryptedSecret = protector.Protect(plain),
            CreatedAt = time.GetUtcNow(),
            CreatedBy = "system:seed",
            PiiPolicy = PiiPolicy.RerouteToOnPrem,
        };
        db.VirtualKeys.Add(key);
        db.Budgets.Add(new Budget { Scope = BudgetScope.VirtualKey, ScopeId = key.Id, LimitSek = 500m, Period = BudgetPeriod.Monthly, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded demo data with {ProviderCount} providers")]
    private static partial void LogSeeded(ILogger logger, int providerCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed:DevKey has an invalid format and was ignored")]
    private static partial void LogInvalidDevKey(ILogger logger);
}
