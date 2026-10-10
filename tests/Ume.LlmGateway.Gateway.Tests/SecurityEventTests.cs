using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Gateway.Pipeline;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class SecurityEventTests(GatewayFixture fixture)
{
    [Fact]
    public async Task Failures_for_a_known_key_are_counted_into_one_row_with_its_owner()
    {
        var key = await fixture.CreateKeyAsync(k => k.RevokedAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        for (var i = 0; i < 3; i++)
        {
            using var response = await fixture.SendAsync(key);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await FlushAsync();
        var row = await QueryAsync(db => db.AuthFailures.SingleAsync(f => f.VirtualKeyId == key.Id, TestContext.Current.CancellationToken));
        row.Reason.ShouldBe(AuthFailureReason.KeyRevoked);
        row.Count.ShouldBe(3);
        row.TeamId.ShouldBe(key.TeamId);
        row.DepartmentId.ShouldBe(fixture.DepartmentId);
        row.KeyPrefix.ShouldNotBeNullOrEmpty();
        key.Secret.ShouldStartWith(row.KeyPrefix!);
        fixture.Logs.Logged("Ume.LlmGateway.Security", "Authentication failed: key_revoked on ChatCompletions x3").ShouldBeTrue(fixture.Logs.Text);
        fixture.Logs.Text.ShouldContain($"key {key.Id}");
    }

    [Fact]
    public async Task Unknown_and_missing_keys_are_recorded_without_the_presented_value()
    {
        // Well-formed but unknown: same shape as a real key, so it reaches the database lookup.
        var unknown = "ume-sk-" + new string('Q', (await fixture.CreateKeyAsync()).Secret.Length - "ume-sk-".Length);
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings") { Content = new StringContent("{}") })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unknown);
            using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var response = await fixture.SendRawAsync(null, "/v1/embeddings", "{}"))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await FlushAsync();
        var rows = await QueryAsync(db => db.AuthFailures.Where(f => f.Endpoint == GatewayEndpoint.Embeddings && f.VirtualKeyId == null)
            .ToListAsync(TestContext.Current.CancellationToken));
        rows.ShouldContain(r => r.Reason == AuthFailureReason.InvalidKey && r.KeyPrefix == null);
        rows.ShouldContain(r => r.Reason == AuthFailureReason.MissingKey);
        fixture.Logs.Text.ShouldNotContain(unknown);
        fixture.Logs.Text.ShouldNotContain(unknown[..12]);
    }

    [Fact]
    public async Task Refused_keys_on_the_models_endpoint_are_labelled_models()
    {
        var key = await fixture.CreateKeyAsync(k => k.IsEnabled = false);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
            using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        await FlushAsync();
        var row = await QueryAsync(db => db.AuthFailures.SingleAsync(f => f.VirtualKeyId == key.Id, TestContext.Current.CancellationToken));
        row.Endpoint.ShouldBe(GatewayEndpoint.Models);
        row.Reason.ShouldBe(AuthFailureReason.KeyDisabled);
    }

    [Fact]
    public async Task Buckets_are_capped_and_the_rest_is_folded_into_one_overflow_row()
    {
        // A unique instant identifies this recorder's rows in the shared database.
        var time = new FakeTimeProvider(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(Random.Shared.Next(1_000_000)));
        var settings = new GatewayOptions { Security = new SecurityEventOptions { SourceAddress = SourceAddressMode.Full, MaxAuthFailureBuckets = 10 } };
        using var recorder = new AuthFailureRecorder(fixture.Services.GetRequiredService<IServiceScopeFactory>(), new StaticOptions(settings),
            time, NullLoggerFactory.Instance, NullLogger<AuthFailureRecorder>.Instance);

        for (var i = 1; i <= 25; i++)
        {
            recorder.Record(AuthFailureReason.InvalidKey, GatewayEndpoint.Responses, null, IPAddress.Parse($"203.0.113.{i}"));
        }

        await recorder.FlushAsync(TestContext.Current.CancellationToken);
        var rows = await QueryAsync(db => db.AuthFailures.Where(f => f.FirstSeen == time.GetUtcNow()).ToListAsync(TestContext.Current.CancellationToken));
        rows.Count.ShouldBe(11);
        rows.Sum(r => r.Count).ShouldBe(25);
        rows.Single(r => r.SourceAddress == null).Count.ShouldBe(15);
    }

    [Theory]
    [InlineData("192.0.2.123", SourceAddressMode.Truncated, "192.0.2.0")]
    [InlineData("192.0.2.123", SourceAddressMode.Full, "192.0.2.123")]
    [InlineData("192.0.2.123", SourceAddressMode.None, null)]
    [InlineData("::ffff:192.0.2.123", SourceAddressMode.Truncated, "192.0.2.0")]
    [InlineData("2001:db8:abcd:1234:5678::1", SourceAddressMode.Truncated, "2001:db8:abcd::")]
    public void Source_address_is_truncated_by_default(string address, SourceAddressMode mode, string? expected) =>
        AuthFailureRecorder.FormatAddress(IPAddress.Parse(address), mode).ShouldBe(expected);

    [Fact]
    public async Task Pii_actions_and_policy_refusals_are_security_events()
    {
        var piiKey = await fixture.CreateKeyAsync(k => k.PiiPolicy = PiiPolicy.Block);
        using var blocked = await fixture.SendAsync(piiKey, prompt: "Personnummer 19121212-1212");
        blocked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var blockedId = blocked.Headers.GetValues("x-request-id").Single();

        var restricted = await fixture.CreateKeyAsync(k => k.AllowedModels = ["onprem/ok"]);
        using var refused = await fixture.SendAsync(restricted, "eu/ok");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var refusedId = refused.Headers.GetValues("x-request-id").Single();

        await fixture.UsageAsync(refused);
        fixture.Logs.Logged("Ume.LlmGateway.Security", $"Personal data blocked in request {blockedId} on ChatCompletions (Personnummer:1)").ShouldBeTrue(fixture.Logs.Text);
        fixture.Logs.Logged("Ume.LlmGateway.Security", $"Request {refusedId} on ChatCompletions refused by policy: model_not_allowed; key {restricted.Id}").ShouldBeTrue(fixture.Logs.Text);
        fixture.Logs.Text.ShouldNotContain("19121212-1212");
    }

    private Task FlushAsync() => fixture.Services.GetRequiredService<AuthFailureRecorder>().FlushAsync(TestContext.Current.CancellationToken);

    private async Task<T> QueryAsync<T>(Func<GatewayDbContext, Task<T>> query)
    {
        using var scope = fixture.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<GatewayDbContext>());
    }

    private sealed class StaticOptions(GatewayOptions value) : IOptionsMonitor<GatewayOptions>
    {
        public GatewayOptions CurrentValue => value;
        public GatewayOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }
}
