using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Gateway.Routing;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Security;
using Ume.LlmGateway.Infrastructure.Stores;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>Failure kinds, provider transport, stores, credentials and catalogue policies of the data plane.</summary>
public sealed class DataPlaneTests(GatewayFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(408, FailureKind.Transient)]
    [InlineData(409, FailureKind.Transient)]
    [InlineData(429, FailureKind.Transient)]
    [InlineData(500, FailureKind.Transient)]
    [InlineData(503, FailureKind.Transient)]
    [InlineData(401, FailureKind.ProviderConfig)]
    [InlineData(403, FailureKind.ProviderConfig)]
    [InlineData(404, FailureKind.ProviderConfig)]
    [InlineData(400, FailureKind.ClientError)]
    [InlineData(422, FailureKind.ClientError)]
    public void Upstream_status_decides_fallback_and_circuit(int status, FailureKind kind)
    {
        var failure = ProviderFailure.FromStatus(status);
        failure.Kind.ShouldBe(kind);
        failure.Reason.ShouldBe($"http_{status}");
        failure.CanFallBack.ShouldBe(kind != FailureKind.ClientError);
        failure.CountsAgainstCircuit.ShouldBe(kind == FailureKind.Transient);
    }

    [Fact]
    public void Only_passing_failures_are_retried_on_the_same_deployment()
    {
        ProviderFailure.FromStatus(502).IsRetryable.ShouldBeTrue();
        ProviderFailure.FromStatus(503).IsRetryable.ShouldBeTrue();
        ProviderFailure.FromStatus(504).IsRetryable.ShouldBeTrue();
        new ProviderFailure(502, FailureKind.Transient, "connection_error:ConnectionError").IsRetryable.ShouldBeTrue();

        ProviderFailure.Timeout.IsRetryable.ShouldBeFalse(); // already waited the whole timeout
        ProviderFailure.FromStatus(503, retryAfter: TimeSpan.FromSeconds(5)).IsRetryable.ShouldBeFalse(); // cooldown instead
        ProviderFailure.FromStatus(429).IsRetryable.ShouldBeFalse();
        ProviderFailure.FromStatus(500).IsRetryable.ShouldBeFalse();
        ProviderFailure.FromStatus(404).IsRetryable.ShouldBeFalse();
        ProviderFailure.InvalidJson.IsRetryable.ShouldBeFalse();
    }

    [Fact]
    public async Task Endpoint_not_supported_falls_back_without_counting_against_the_circuit()
    {
        using var http = new ProviderHttpClient(new NoSendHandler());
        var provider = new ProviderAccount { Name = "p", BaseUrl = "https://x/v1", Type = ProviderType.OpenAI };
        var call = new ProviderCall(provider, new ModelDeployment { Name = "m", UpstreamModel = "m" }, GatewayEndpoint.AnthropicMessages, new JsonObject(), false, null, false);
        var openai = (await new OpenAICompatibleAdapter(http).SendAsync(call, Ct)).ShouldBeOfType<ProviderFailure>();
        var anthropic = (await new AnthropicAdapter(http, TimeProvider.System).SendAsync(call with { Endpoint = GatewayEndpoint.Embeddings }, Ct)).ShouldBeOfType<ProviderFailure>();
        var (_, realtime) = await new RealtimeConnector(http).ConnectAsync(
            new RealtimeCall(new ProviderAccount { Name = "a", BaseUrl = "https://x/v1", Type = ProviderType.Anthropic }, call.Deployment, GatewayEndpoint.Realtime, null, [], []), Ct);
        foreach (var failure in new[] { openai, anthropic, realtime! })
        {
            failure.Kind.ShouldBe(FailureKind.Unsupported);
            failure.CanFallBack.ShouldBeTrue();
            failure.CountsAgainstCircuit.ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(ProviderAuthMode.Bearer, ProviderType.OpenAI, "Authorization", "Bearer s3cret")]
    [InlineData(ProviderAuthMode.ApiKeyHeader, ProviderType.AzureOpenAI, "api-key", "s3cret")]
    [InlineData(ProviderAuthMode.XApiKeyHeader, ProviderType.Anthropic, "x-api-key", "s3cret")]
    public void Provider_auth_puts_the_credential_in_the_right_header(ProviderAuthMode mode, ProviderType type, string header, string value)
    {
        var provider = new ProviderAccount { Name = "p", BaseUrl = "https://x", AuthMode = mode, Type = type };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ProviderAuth.Apply(provider, "s3cret", headers, static (h, name, v) => h[name] = v);
        headers[header].ShouldBe(value);
        headers.ContainsKey(ProviderAuth.AnthropicVersionHeader).ShouldBe(type == ProviderType.Anthropic);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://x");
        ProviderAuth.Apply(request, provider, "s3cret");
        request.Headers.GetValues(header).Single().ShouldBe(value);

        var none = new Dictionary<string, string>();
        ProviderAuth.Apply(provider, null, none, static (h, name, v) => h[name] = v);
        none.Keys.ShouldNotContain(header);
    }

    [Fact]
    public void Transport_rules_are_shared_and_named()
    {
        var provider = new ProviderAccount { Name = "p", BaseUrl = "https://x", TimeoutSeconds = 5000 };
        ProviderTransport.RequestTimeout(provider).ShouldBe(TimeSpan.FromSeconds(ProviderTransport.MaxRequestTimeoutSeconds));
        ProviderTransport.RealtimeConnectTimeout(provider).ShouldBe(TimeSpan.FromSeconds(ProviderTransport.MaxRealtimeConnectTimeoutSeconds));
        provider.TimeoutSeconds = 0;
        ProviderTransport.RequestTimeout(provider).ShouldBe(TimeSpan.FromSeconds(1));
        ProviderTransport.IsPermitted(new Uri("http://onprem/v1"), requireHttps: true).ShouldBeFalse();
        ProviderTransport.IsPermitted(new Uri("ws://onprem/v1"), requireHttps: true).ShouldBeFalse();
        ProviderTransport.IsPermitted(new Uri("wss://onprem/v1"), requireHttps: true).ShouldBeTrue();
        ProviderTransport.IsPermitted(new Uri("http://onprem/v1"), requireHttps: false).ShouldBeTrue();
        ProviderTransport.UserAgent.Product!.Version.ShouldNotBe("0.1");
        ProviderTransport.UserAgent.Product.Version!.ShouldNotContain("+");
    }

    [Fact]
    public void Adapter_lookup_takes_the_first_adapter_per_type()
    {
        using var http = new ProviderHttpClient(new NoSendHandler());
        var anthropic = new AnthropicAdapter(http, TimeProvider.System);
        var openai = new OpenAICompatibleAdapter(http);
        var lookup = new ProviderAdapterLookup([anthropic, openai]);
        lookup.Resolve(ProviderType.Anthropic).ShouldBeSameAs(anthropic);
        lookup.Resolve(ProviderType.Ollama).ShouldBeSameAs(openai);
        Should.Throw<InvalidOperationException>(() => new ProviderAdapterLookup([anthropic]).Resolve(ProviderType.OpenAI));
    }

    [Fact]
    public void Redis_keys_stay_inside_the_acl_namespace()
    {
        var id = Guid.NewGuid();
        string[] keys =
        [
            RedisKeys.RateRequests(id, 1), RedisKeys.RateTokens(id, 1), RedisKeys.RealtimeSessions(id), RedisKeys.CircuitOpen(id),
            RedisKeys.CircuitFailures(id), RedisKeys.Cooldown(id), RedisKeys.InFlight(id), RedisKeys.UsageJournalIndex, RedisKeys.UsageJournalRecords,
            RedisKeys.UsageJournalBatch("b"), RedisKeys.Spend(BudgetScope.Team, id, BudgetPeriod.Daily, DateTimeOffset.UnixEpoch),
            SpendCounter.KeyFor(BudgetScope.Team, id, BudgetPeriod.Daily, DateTimeOffset.UnixEpoch),
        ];
        keys.ShouldAllBe(k => k.StartsWith("ume:", StringComparison.Ordinal));
        keys.Distinct().Count().ShouldBe(keys.Length - 1); // KeyFor is the spend key
        RedisKeys.InvalidateChannelName.ShouldBe("ume:invalidate");
        RedisKeys.InvalidateChannel.ToString().ShouldBe("ume:invalidate");
    }

    [Fact]
    public async Task In_memory_rate_limiter_drops_old_windows()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var limiter = new InMemoryRateLimiter(time);
        for (var minute = 0; minute < 5; minute++)
        {
            await limiter.AcquireAsync(Guid.NewGuid(), 10, 100, Ct);
            await limiter.RecordTokensAsync(Guid.NewGuid(), 5, Ct);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        await limiter.AcquireAsync(Guid.NewGuid(), 10, 100, Ct);
        // The current and the previous minute are kept (previous: one request and one token counter).
        limiter.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Rate_limit_slides_across_the_minute_boundary()
    {
        // 30 s into a minute, a key with 10 requests per minute uses all 10.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(30));
        var limiter = new InMemoryRateLimiter(time);
        var key = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
        {
            (await limiter.AcquireAsync(key, 10, null, Ct)).Allowed.ShouldBeTrue();
        }

        var refused = await limiter.AcquireAsync(key, 10, null, Ct);
        refused.Allowed.ShouldBeFalse();
        refused.Reason.ShouldBe("requests");

        // A fixed window would allow 10 more right after the boundary; the last 60 s still hold about 10.
        time.Advance(TimeSpan.FromSeconds(31));
        (await limiter.AcquireAsync(key, 10, null, Ct)).Allowed.ShouldBeFalse();

        // Half a minute later half of the previous minute has slid out: 5 fit (10 × 0.5 + 5 = 10).
        time.Advance(TimeSpan.FromSeconds(30));
        var admitted = 0;
        while ((await limiter.AcquireAsync(key, 10, null, Ct)).Allowed)
        {
            admitted++;
        }

        admitted.ShouldBeInRange(4, 5);
    }

    [Fact]
    public void Retry_after_is_when_the_sliding_count_allows_one_more()
    {
        // 10 last minute, none this minute, limit 10, 6 s in: one fits once a tenth of last minute slid out (now).
        RateLimitWindow.RetryAfter(10, 0, 1, 10, 0.1).ShouldBe(TimeSpan.FromSeconds(1));
        // 10 last minute, 5 this minute, 30 s in: 10 × (1 − f) + 6 ≤ 10 at f = 0.6, 6 s from now.
        RateLimitWindow.RetryAfter(10, 5, 1, 10, 0.5).ShouldBe(TimeSpan.FromSeconds(6));
        // This minute alone is full: wait for the next minute, plus until enough of this one has slid out.
        RateLimitWindow.RetryAfter(0, 10, 1, 10, 0.5).ShouldBe(TimeSpan.FromSeconds(36));
    }

    [Fact]
    public async Task Concurrency_limit_counts_requests_in_flight_until_released()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var limiter = new InMemoryRateLimiter(time);
        var key = Guid.NewGuid();
        (await limiter.AcquireAsync(key, null, null, 2, "a", Ct)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(key, null, null, 2, "b", Ct)).Allowed.ShouldBeTrue();
        var refused = await limiter.AcquireAsync(key, null, null, 2, "c", Ct);
        refused.Allowed.ShouldBeFalse();
        refused.Reason.ShouldBe("concurrency");

        limiter.Release(key, "a");
        (await limiter.AcquireAsync(key, null, null, 2, "c", Ct)).Allowed.ShouldBeTrue();

        // A slot that is never released (the instance died) expires.
        time.Advance(RateLimitWindow.InFlightLease);
        (await limiter.AcquireAsync(key, null, null, 2, "d", Ct)).Allowed.ShouldBeTrue();
        (await limiter.AcquireAsync(key, null, null, 2, "e", Ct)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Credentials_are_decrypted_once_per_snapshot()
    {
        var provider = new CountingProvider(new EphemeralDataProtectionProvider());
        var protector = new CredentialProtector(provider);
        var account = new ProviderAccount { Name = "p", BaseUrl = "https://x", EncryptedCredential = protector.Protect("s3cret") };
        var first = new ProviderCredentials();
        first.Get(account, protector).ShouldBe("s3cret");
        first.Get(account, protector).ShouldBe("s3cret");
        provider.Unprotects.ShouldBe(1);

        new ProviderCredentials().Get(account, protector).ShouldBe("s3cret");
        provider.Unprotects.ShouldBe(2);
        Should.Throw<System.Security.Cryptography.CryptographicException>(() =>
            new ProviderCredentials().Get(new ProviderAccount { Name = "q", BaseUrl = "https://x", EncryptedCredential = "not-a-ciphertext" }, protector));
    }

    [Fact]
    public async Task Models_list_hides_models_no_endpoint_can_reach()
    {
        var key = await fixture.CreateKeyAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        using var response = await fixture.Client.SendAsync(request, Ct);
        var ids = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToList();
        ids.ShouldContain("eu/embedding");
        ids.ShouldNotContain("anthropic/embedding"); // the Anthropic provider has no embeddings capability
        ids.ShouldContain("speech/whisper");
    }

    [Fact]
    public async Task Model_allow_list_is_case_insensitive()
    {
        using var response = await fixture.SendAsync(await fixture.CreateKeyAsync(k => k.AllowedModels = ["EU/OK"]), "eu/ok");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed class NoSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No request expected");
    }

    private sealed class CountingProvider(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public int Unprotects;

        public IDataProtector CreateProtector(string purpose) => new Counting(inner.CreateProtector(purpose), this);

        private sealed class Counting(IDataProtector inner, CountingProvider owner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new Counting(inner.CreateProtector(purpose), owner);
            public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);

            public byte[] Unprotect(byte[] protectedData)
            {
                Interlocked.Increment(ref owner.Unprotects);
                return inner.Unprotect(protectedData);
            }
        }
    }
}
