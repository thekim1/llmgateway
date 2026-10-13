using System.Net;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Mcp;

namespace Ume.LlmGateway.Gateway.Tests;

/// <summary>A token endpoint that fails is not the MCP server failing: the server is never asked and its circuit is not charged.</summary>
public sealed class McpUpstreamTokenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(null)]
    public async Task A_failing_token_endpoint_is_reported_as_such_without_calling_the_server(HttpStatusCode? tokenStatus)
    {
        var token = new Recording(_ => tokenStatus is { } status ? new HttpResponseMessage(status) : throw new HttpRequestException("unreachable"));
        var server = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var upstream = new McpUpstream(new Handlers(server), new McpTokenCache(new Clients(token), TimeProvider.System), new McpUpstreamOptions());
        var auth = McpUpstreamAuth.Parse(McpAuthType.OAuthClientCredentials,
            """{"tokenUrl":"https://id.example/token","clientId":"c","clientSecret":"s"}""");
        var mcpServer = new McpServer { Slug = "oauth", DisplayName = "OAuth", Url = "https://mcp.example/mcp", AuthType = McpAuthType.OAuthClientCredentials };

        var result = await upstream.SendAsync(new McpUpstreamCall(mcpServer, auth, "tools/list", new JsonObject(), [], MayRetry: true, null), Ct);

        result.Failure.ShouldBe(McpUpstreamFailure.TokenEndpoint);
        result.HttpStatus.ShouldBe((int?)tokenStatus);
        result.CountsAgainstCircuit.ShouldBeFalse();
        result.Attempts.ShouldBe(1);
        server.Calls.ShouldBe(0);
    }

    /// <summary>Answers every request with <c>answer</c> and counts them, across the handlers the factories create.</summary>
    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        private int _calls;

        public int Calls => _calls;

        public HttpMessageHandler Handler() => new Answering(this);

        private sealed class Answering(Recording owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._calls);
                return Task.FromResult(owner.Answer(request));
            }
        }

        private HttpResponseMessage Answer(HttpRequestMessage request) => answer(request);
    }

    private sealed class Handlers(Recording recording) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => recording.Handler();
    }

    private sealed class Clients(Recording recording) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(recording.Handler());
    }
}
