using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Providers;
using Ume.LlmGateway.Infrastructure.Providers.OpenAI;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class ProviderTransportTests
{
    [Fact]
    public async Task Production_never_sends_content_or_credentials_to_plain_http_even_on_prem()
    {
        using var handler = new RejectSendHandler();
        using var http = new ProviderHttpClient(handler, requireHttps: true);
        var adapter = new OpenAICompatibleAdapter(http);
        var call = new ProviderCall(
            new ProviderAccount { Name = "test", BaseUrl = "http://onprem/v1", Type = ProviderType.OpenAICompatible },
            new ModelDeployment { Name = "test", UpstreamModel = "test" }, GatewayEndpoint.ChatCompletions,
            new JsonObject { ["model"] = "test" }, false, null, false);
        var failure = (await adapter.SendAsync(call, TestContext.Current.CancellationToken)).ShouldBeOfType<ProviderFailure>();
        failure.Reason.ShouldBe("https_required");
        failure.Kind.ShouldBe(FailureKind.ProviderConfig);
        failure.CanFallBack.ShouldBeTrue();
        failure.CountsAgainstCircuit.ShouldBeFalse();
    }

    [Fact]
    public void Retry_after_is_read_from_milliseconds_seconds_or_a_date()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        using var ms = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        ms.Headers.TryAddWithoutValidation("retry-after-ms", "1500");
        ms.Headers.TryAddWithoutValidation("Retry-After", "2");
        ProviderTransport.RetryAfter(ms, now).ShouldBe(TimeSpan.FromMilliseconds(1500)); // the precise one wins

        using var seconds = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        seconds.Headers.TryAddWithoutValidation("Retry-After", "30");
        ProviderTransport.RetryAfter(seconds, now).ShouldBe(TimeSpan.FromSeconds(30));

        using var date = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        date.Headers.TryAddWithoutValidation("Retry-After", now.AddSeconds(45).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        ProviderTransport.RetryAfter(date, now).ShouldBe(TimeSpan.FromSeconds(45));

        using var past = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        past.Headers.TryAddWithoutValidation("Retry-After", now.AddSeconds(-5).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        ProviderTransport.RetryAfter(past, now).ShouldBeNull();

        using var none = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        none.Headers.TryAddWithoutValidation("retry-after-ms", "soon");
        ProviderTransport.RetryAfter(none, now).ShouldBeNull();
    }

    [Fact]
    public async Task Retry_after_reaches_the_failure()
    {
        using var handler = new StatusHandler(System.Net.HttpStatusCode.TooManyRequests, "12");
        using var http = new ProviderHttpClient(handler);
        var adapter = new OpenAICompatibleAdapter(http);
        var call = new ProviderCall(
            new ProviderAccount { Name = "test", BaseUrl = "https://provider/v1", Type = ProviderType.OpenAICompatible },
            new ModelDeployment { Name = "test", UpstreamModel = "test" }, GatewayEndpoint.ChatCompletions,
            new JsonObject { ["model"] = "test" }, false, null, false);
        var failure = (await adapter.SendAsync(call, TestContext.Current.CancellationToken)).ShouldBeOfType<ProviderFailure>();
        failure.StatusCode.ShouldBe(429);
        failure.Kind.ShouldBe(FailureKind.Transient);
        failure.RetryAfter.ShouldBe(TimeSpan.FromSeconds(12));
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode status, string retryAfter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"message":"slow down"}}""") };
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        }
    }

    private sealed class RejectSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Plain HTTP must not be sent");
    }
}
