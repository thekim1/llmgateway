using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Providers;

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
        failure.Retryable.ShouldBeTrue();
    }

    private sealed class RejectSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Plain HTTP must not be sent");
    }
}
