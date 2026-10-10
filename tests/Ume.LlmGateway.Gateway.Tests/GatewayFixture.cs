using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Domain.Services;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;
using Ume.LlmGateway.Infrastructure.Security;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

[assembly: AssemblyFixture(typeof(Ume.LlmGateway.Gateway.Tests.GatewayFixture))]

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class GatewayFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly Dictionary<string, ModelDeployment> _models = new(StringComparer.Ordinal);
    private WebApplicationFactory<Program> _factory = null!;
    public WireMockServer Upstream { get; private set; } = null!;
    public RealtimeUpstream Realtime { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public CapturingLoggerProvider Logs { get; } = new();
    public IServiceProvider Services => _factory.Services;
    public Guid DepartmentId { get; private set; }
    private readonly string _keyPepper = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); // fixed, so derived gateways accept the same keys
    public string ProviderSecret { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        Upstream = WireMockServer.Start();
        await Realtime.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gatewaydb", _postgres.GetConnectionString());
            builder.UseSetting("Security:KeyPepper", _keyPepper);
            builder.UseSetting("Gateway:KeyCacheSeconds", "0");
            builder.UseSetting("Gateway:CatalogCacheSeconds", "0");
            builder.UseSetting("Gateway:MaxRequestBodyBytes", "4096");
            builder.UseSetting("Gateway:MaxAudioRequestBodyBytes", (256 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Gateway:Realtime:BudgetCheckSeconds", "1");
            builder.UseSetting("Gateway:Realtime:MaxSessionsPerKey", "2");
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
                logging.AddProvider(Logs);
            });
        });
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            await db.Database.MigrateAsync();
            var department = new Department { Name = "Tests", CostCenterCode = "TEST", CreatedAt = DateTimeOffset.UtcNow };
            DepartmentId = department.Id;
            db.Departments.Add(department);
            db.ExchangeRates.Add(new ExchangeRate { SekPerUnit = 10, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) });
            var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
            foreach (var (name, residency, type) in new[]
            {
                ("onprem", DataResidency.OnPrem, ProviderType.OpenAICompatible),
                ("eu", DataResidency.Eu, ProviderType.OpenAICompatible),
                ("external", DataResidency.External, ProviderType.OpenAICompatible),
                ("anthropic", DataResidency.External, ProviderType.Anthropic),
                ("slow", DataResidency.Eu, ProviderType.OpenAICompatible),
            })
            {
                var provider = new ProviderAccount
                {
                    Name = name, Type = type, Residency = residency, BaseUrl = Upstream.Url + "/v1",
                    AuthMode = type == ProviderType.Anthropic ? ProviderAuthMode.XApiKeyHeader : ProviderAuthMode.Bearer,
                    EncryptedCredential = protector.Protect(ProviderSecret),
                    Capabilities = ProviderCapabilities.ChatCompletions | ProviderCapabilities.Streaming |
                        (type == ProviderType.Anthropic ? ProviderCapabilities.AnthropicMessages : ProviderCapabilities.Embeddings | ProviderCapabilities.Responses),
                    TimeoutSeconds = name == "slow" ? 1 : 10, CreatedAt = DateTimeOffset.UtcNow,
                };
                foreach (var model in name == "slow" ? new[] { "slow" } : new[] { "ok", "fail503", "fail429", "bad400", "embedding" })
                {
                    var deployment = new ModelDeployment
                    {
                        Name = name + "/" + model, UpstreamModel = name + "-" + model,
                        Kind = model == "embedding" ? ModelKind.Embedding : ModelKind.Chat,
                        Prices = [new ModelPrice { InputPerMillionUsd = 1, CachedInputPerMillionUsd = 0.5m, OutputPerMillionUsd = 2, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) }],
                    };
                    provider.Deployments.Add(deployment);
                    _models.Add(deployment.Name, deployment);
                    Map(deployment.UpstreamModel, type, model);
                }
                db.ProviderAccounts.Add(provider);
            }

            // Speech to text: one provider per upstream behaviour, told apart by base path (multipart bodies are not matched).
            foreach (var (name, model, price) in new[]
            {
                ("speech", "whisper", new ModelPrice { AudioPerMinuteUsd = 0.006m }),
                ("speechtext", "whisper", new ModelPrice { AudioPerMinuteUsd = 0.006m }),
                ("speechtokens", "transcribe", new ModelPrice { InputPerMillionUsd = 6, OutputPerMillionUsd = 10 }),
                ("speechfail", "whisper", new ModelPrice { AudioPerMinuteUsd = 0.006m }),
            })
            {
                price.EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1);
                var deployment = new ModelDeployment { Name = name + "/" + model, UpstreamModel = name + "-" + model, Kind = ModelKind.Transcription, Prices = [price] };
                db.ProviderAccounts.Add(new ProviderAccount
                {
                    Name = name, Type = ProviderType.OpenAICompatible, Residency = DataResidency.Eu, BaseUrl = $"{Upstream.Url}/{name}/v1",
                    AuthMode = ProviderAuthMode.Bearer, EncryptedCredential = protector.Protect(ProviderSecret),
                    Capabilities = ProviderCapabilities.AudioTranscriptions | ProviderCapabilities.Streaming, TimeoutSeconds = 10,
                    CreatedAt = DateTimeOffset.UtcNow, Deployments = [deployment],
                });
                _models.Add(deployment.Name, deployment);
            }

            MapSpeech();

            // Live audio: providers on the scripted WebSocket upstream, told apart by base path (livefail refuses the handshake).
            foreach (var (name, residency) in new[] { ("live", DataResidency.Eu), ("livefail", DataResidency.Eu), ("liveonprem", DataResidency.OnPrem) })
            {
                var provider = new ProviderAccount
                {
                    Name = name, Type = ProviderType.OpenAICompatible, Residency = residency, BaseUrl = $"{Realtime.Url}/{name}/v1",
                    AuthMode = ProviderAuthMode.Bearer, EncryptedCredential = protector.Protect(ProviderSecret),
                    Capabilities = ProviderCapabilities.Realtime | ProviderCapabilities.AudioTranscriptions, TimeoutSeconds = 10, CreatedAt = DateTimeOffset.UtcNow,
                };
                foreach (var (model, kind, price) in new[]
                {
                    ("whisper", ModelKind.Transcription, new ModelPrice { AudioPerMinuteUsd = 0.006m }),
                    ("realtime", ModelKind.Realtime, new ModelPrice { InputPerMillionUsd = 4, CachedInputPerMillionUsd = 0.4m, OutputPerMillionUsd = 16, AudioInputPerMillionUsd = 32, AudioOutputPerMillionUsd = 64 }),
                    ("interpret", ModelKind.SpeechTranslation, new ModelPrice { AudioPerMinuteUsd = 0.02m }),
                })
                {
                    price.EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1);
                    var deployment = new ModelDeployment { Name = name + "/" + model, UpstreamModel = name + "-" + model, Kind = kind, Prices = [price] };
                    provider.Deployments.Add(deployment);
                    _models.Add(deployment.Name, deployment);
                }

                db.ProviderAccounts.Add(provider);
            }

            await db.SaveChangesAsync();
        }
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    }

    /// <summary>A second gateway on the same database and upstreams, with some services replaced. Dispose it.</summary>
    public WebApplicationFactory<Program> Derive(Action<IServiceCollection> configure) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(configure));

    private void Map(string model, ProviderType type, string behavior)
    {
        var path = type == ProviderType.Anthropic ? "/v1/messages" : "/v1/*";
        var request = Request.Create().WithPath(path).UsingPost()
            .WithBody(new JsonPartialMatcher(new { model }));
        var response = Response.Create().WithHeader("Content-Type", "application/json");
        if (behavior.StartsWith("fail", StringComparison.Ordinal) || behavior == "bad400")
        {
            var status = behavior == "fail503" ? 503 : behavior == "fail429" ? 429 : 400;
            response.WithStatusCode(status).WithBody("""{"error":{"type":"invalid_request_error","message":"Upstream rejected request"}}""");
        }
        else
        {
            response.WithStatusCode(200).WithBody(type == ProviderType.Anthropic
                ? """{"id":"msg_test","type":"message","role":"assistant","model":"claude-test","content":[{"type":"text","text":"Test answer"}],"stop_reason":"end_turn","usage":{"input_tokens":100,"output_tokens":20}}"""
                : """{"id":"chatcmpl-test","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Test answer"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":20,"prompt_tokens_details":{"cached_tokens":10}}}""");
            if (behavior == "slow")
            {
                response.WithDelay(TimeSpan.FromSeconds(3));
            }
        }
        Upstream.Given(request).AtPriority(10).RespondWith(response);
        if (type != ProviderType.Anthropic && behavior == "ok")
        {
            Upstream.Given(Request.Create().WithPath("/v1/chat/completions").UsingPost()
                .WithBody(new JsonPartialMatcher(new { model, stream = true })))
                .AtPriority(1)
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream")
                    .WithBody("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Test answer\"}}]}\n\n" +
                        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":10}}}\n\n" +
                        "data: [DONE]\n\n"));
        }
    }

    private void MapSpeech()
    {
        IRequestBuilder Speech(string provider) => Request.Create().WithPath($"/{provider}/v1/audio/*").UsingPost();
        Upstream.Given(Speech("speech")).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
            .WithBody("""{"text":"Hej från Umeå","usage":{"type":"duration","seconds":90}}"""));
        Upstream.Given(Speech("speechtext")).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/plain; charset=utf-8")
            .WithBody("Hej från Umeå\n"));
        Upstream.Given(Speech("speechtokens")).AtPriority(10).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
            .WithBody("""{"text":"Hej","usage":{"type":"tokens","input_tokens":1000,"output_tokens":50,"total_tokens":1050,"input_token_details":{"audio_tokens":1000,"text_tokens":0}}}"""));
        Upstream.Given(Speech("speechtokens").WithHeader("Accept", "*text/event-stream*")).AtPriority(1).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "text/event-stream")
            .WithBody("""
                data: {"type":"transcript.text.delta","delta":"Hej"}

                data: {"type":"transcript.text.done","text":"Hej","usage":{"type":"tokens","input_tokens":1000,"output_tokens":50,"total_tokens":1050}}


                """));
        Upstream.Given(Speech("speechfail")).RespondWith(Response.Create().WithStatusCode(503).WithHeader("Content-Type", "application/json")
            .WithBody("""{"error":{"message":"Fake provider unavailable","type":"server_error"}}"""));
    }

    /// <summary>A PCM WAV file (16 kHz, mono, 16-bit) of the given length; its header gives the exact duration.</summary>
    public static byte[] Wav(double seconds)
    {
        const int byteRate = 16_000 * 2;
        var dataLength = (int)(seconds * byteRate);
        var wav = new byte[44 + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLength);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], 16_000);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        return wav;
    }

    /// <summary>Posts an OpenAI-style multipart upload; a null <paramref name="file"/> leaves the file part out.</summary>
    public async Task<HttpResponseMessage> SendAudioAsync(TestKey? key, string model, byte[]? file, string endpoint = "/v1/audio/transcriptions",
        IDictionary<string, string>? fields = null, string fileName = "samtal.wav")
    {
        using var form = new MultipartFormDataContent { { new StringContent(model), "model" } };
        foreach (var (name, value) in fields ?? new Dictionary<string, string>())
        {
            form.Add(new StringContent(value), name);
        }

        if (file is not null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(content, "file", fileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        }

        return await Client.SendAsync(request);
    }

    /// <summary>Opens a live session through the gateway; a refused handshake throws with the status code in the message.</summary>
    public async Task<System.Net.WebSockets.WebSocket> ConnectRealtimeAsync(TestKey? key, string model, string path = "/v1/realtime", string query = "",
        IDictionary<string, string>? headers = null)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (key is not null)
            {
                request.Headers.Authorization = "Bearer " + key.Secret;
            }

            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                request.Headers[name] = value;
            }
        };
        return await client.ConnectAsync(new Uri($"ws://localhost{path}?model={Uri.EscapeDataString(model)}{query}"), TestContext.Current.CancellationToken);
    }

    /// <summary>The one usage record of a key, once the writer has flushed.</summary>
    public async Task<UsageRecord> UsageForKeyAsync(Guid keyId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        UsageRecord? record = null;
        while (record is null)
        {
            await Services.GetRequiredService<UsageWriter>().FlushAsync(cts.Token);
            using var scope = Services.CreateScope();
            record = await scope.ServiceProvider.GetRequiredService<GatewayDbContext>().UsageRecords.SingleOrDefaultAsync(u => u.VirtualKeyId == keyId, cts.Token);
            if (record is null)
            {
                await Task.Delay(50, cts.Token); // the session is still being accounted after the close
            }
        }

        return record;
    }

    public async Task<TestKey> CreateKeyAsync(Action<VirtualKey>? configure = null)
    {
        using var scope = Services.CreateScope();
        var generated = scope.ServiceProvider.GetRequiredService<VirtualKeyHasher>().Generate();
        var team = new Team { Name = Guid.NewGuid().ToString(), DepartmentId = DepartmentId, CreatedAt = DateTimeOffset.UtcNow };
        var key = new VirtualKey { Team = team, TeamId = team.Id, Name = "Test key", Prefix = generated.Prefix, KeyHash = generated.Hash, CreatedAt = DateTimeOffset.UtcNow };
        configure?.Invoke(key);
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.VirtualKeys.Add(key);
        await db.SaveChangesAsync();
        return new TestKey(key.Id, team.Id, generated.PlainText);
    }

    public async Task<string> CreateRouteAsync(params string[] models)
    {
        var route = new RouteAlias
        {
            Name = "test/" + Guid.NewGuid(), Kind = _models[models[0]].Kind,
            Targets = [.. models.Select((m, i) => new RouteTarget { ModelDeploymentId = _models[m].Id, Priority = i })],
        };
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.RouteAliases.Add(route);
        await db.SaveChangesAsync();
        return route.Name;
    }

    public Task AddBudgetAsync(BudgetScope scopeType, Guid id, decimal limit) => AddBudgetAsync(scopeType, id, limit, BudgetPeriod.Monthly);

    public async Task AddBudgetAsync(BudgetScope scopeType, Guid id, decimal limit, BudgetPeriod period)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        db.Budgets.Add(new Budget { Scope = scopeType, ScopeId = id, LimitSek = limit, Period = period, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    public Task<HttpResponseMessage> SendAsync(TestKey? key, string model = "eu/ok", string endpoint = "/v1/chat/completions", string prompt = "Test prompt", bool stream = false, bool includeUsage = false, IDictionary<string, string>? headers = null)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = 20, ["stream"] = stream,
        };
        if (includeUsage)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        return SendRawAsync(key, endpoint, body.ToJsonString(), headers: headers);
    }

    public async Task<HttpResponseMessage> SendRawAsync(TestKey? key, string endpoint, string body, string contentType = "application/json", IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.Add(name, value);
        }

        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        }
        return await Client.SendAsync(request);
    }

    public async Task<UsageRecord> UsageAsync(HttpResponseMessage response)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Services.GetRequiredService<UsageWriter>().FlushAsync(cts.Token);
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var requestId = response.Headers.GetValues("x-request-id").Single();
        return await db.UsageRecords.SingleAsync(u => u.RequestId == requestId, cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        Upstream?.Dispose();
        await Realtime.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed record TestKey(Guid Id, Guid TeamId, string Secret);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    public string Text => string.Join("\n", _messages);
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages);
    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(category + ": " + formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
    }
}
