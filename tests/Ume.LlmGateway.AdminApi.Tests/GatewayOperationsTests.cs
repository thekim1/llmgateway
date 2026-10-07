using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi;
using Ume.LlmGateway.Infrastructure.Security;

namespace Ume.LlmGateway.AdminApi.Tests;

public sealed class GatewayOperationsTests
{
    private static readonly string Pepper = new('p', 48);

    [Fact]
    public async Task Probe_signs_request_and_preserves_live_metadata()
    {
        var status = new GatewayOperationsStatus("Degraded", "10.20.30", new UsageWriterStatus(17, 10000, 5, DateTimeOffset.UtcNow, 2));
        using var handler = new ProbeHandler(request =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe(OperationsSignature.Path);
            OperationsSignature.Verify(Pepper, request.Headers.GetValues(OperationsSignature.TimestampHeader).Single(),
                request.Headers.GetValues(OperationsSignature.SignatureHeader).Single(), DateTimeOffset.UtcNow).ShouldBeTrue();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(status) };
        });
        var result = await Client(handler).ReadAsync(TestContext.Current.CancellationToken);
        result.ShouldBe(status);
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(503, "{}")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(200, """{"status":"Healthy","version":"1","usageWriter":{"queueDepth":-1,"capacity":10000}}""")]
    public async Task Failed_or_malformed_probe_is_unavailable_not_fabricated(int code, string body)
    {
        using var handler = new ProbeHandler(_ => new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) });
        (await Client(handler).ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Http_address_is_rejected_without_sending_signature()
    {
        using var handler = new ProbeHandler(_ => throw new InvalidOperationException("Must not send"));
        (await Client(handler, "http://gateway").ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Network_failure_is_reported_as_unavailable()
    {
        using var handler = new ProbeHandler(_ => throw new HttpRequestException("Network failure"));
        (await Client(handler).ReadAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private static GatewayOperationsClient Client(HttpMessageHandler handler, string address = "https://gateway")
    {
        var factory = new ProbeClientFactory(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:OperationsUrl"] = address }).Build();
        return new GatewayOperationsClient(factory, configuration, Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper }),
            TimeProvider.System, NullLogger<GatewayOperationsClient>.Instance);
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class ProbeClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            name.ShouldBe("gateway-operations");
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
