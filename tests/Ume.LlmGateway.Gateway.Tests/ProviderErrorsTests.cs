using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Gateway.Requests;

namespace Ume.LlmGateway.Gateway.Tests;

public sealed class ProviderErrorsTests
{
    [Fact]
    public void Only_message_type_code_and_param_are_passed_on()
    {
        const string provider = """
            {"error":{"message":"Invalid value for 'temperature'.","type":"invalid_request_error","code":"invalid_value","param":"temperature",
              "innererror":{"deployment":"internal-deployment-7","region":"swedencentral"}},"organization":"org-internal-42"}
            """;

        var body = ProviderErrors.ClientBody(GatewayEndpoint.ChatCompletions, provider, "req-1");

        JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse("""
            {"error":{"message":"Invalid value for 'temperature'.","type":"invalid_request_error","code":"invalid_value","param":"temperature","request_id":"req-1"}}
            """)).ShouldBeTrue(body);
    }

    [Fact]
    public void Anthropic_clients_get_the_anthropic_shape()
    {
        const string provider = """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: too large"},"request_id":"req_upstream"}""";

        var body = JsonNode.Parse(ProviderErrors.ClientBody(GatewayEndpoint.AnthropicMessages, provider, "req-1"))!;

        body["type"]!.GetValue<string>().ShouldBe("error");
        body["error"]!["message"]!.GetValue<string>().ShouldBe("max_tokens: too large");
        body["request_id"]!.GetValue<string>().ShouldBe("req-1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>Bad Request from proxy-internal-3</body></html>")]
    [InlineData("""{"detail":{"nested":true}}""")]
    public void Unreadable_bodies_get_the_default_message(string? provider)
    {
        var body = ProviderErrors.ClientBody(GatewayEndpoint.ChatCompletions, provider, "req-1");

        var error = JsonNode.Parse(body)!["error"]!;
        error["message"]!.GetValue<string>().ShouldBe(ProviderErrors.DefaultMessage);
        error["code"]!.GetValue<string>().ShouldBe(GatewayErrorCodes.ProviderRejected);
        body.ShouldNotContain("proxy-internal-3");
    }

    [Fact]
    public void Flat_bodies_numeric_codes_control_characters_and_long_messages_are_handled()
    {
        var provider = new JsonObject { ["message"] = "line one\nline two\u001b[2J" + new string('x', 5000), ["code"] = 400 }.ToJsonString();

        var error = JsonNode.Parse(ProviderErrors.ClientBody(GatewayEndpoint.ChatCompletions, provider, "req-1"))!["error"]!;

        var message = error["message"]!.GetValue<string>();
        message.ShouldStartWith("line one line two [2J");
        message.Length.ShouldBe(1000);
        error["code"]!.GetValue<string>().ShouldBe("400");
    }
}
