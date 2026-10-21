using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public sealed class ProviderFeaturePolicyTests
{
    private static JsonObject Body(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, """{"tools":[{"type":"function","function":{"name":"lookup"}}]}""")]
    [InlineData(GatewayEndpoint.ChatCompletions, """{"tools":[{"type":"custom","custom":{"name":"grammar"}}]}""")]
    [InlineData(GatewayEndpoint.Responses, """{"tools":[{"type":"function","name":"lookup"}]}""")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"tools":[{"name":"lookup","input_schema":{}},{"type":"custom","name":"x"}]}""")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"tools":[{"type":"bash_20250124","name":"bash"},{"type":"text_editor_20250728","name":"e"},{"type":"computer_20250124","name":"c"},{"type":"memory_20250818","name":"m"}]}""")]
    [InlineData(GatewayEndpoint.ChatCompletions, """{"web_search_options":null,"tools":[]}""")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"mcp_servers":[]}""")]
    public void Tools_the_client_runs_are_accepted(GatewayEndpoint endpoint, string json) =>
        ProviderFeaturePolicy.Apply(Body(json), endpoint, allowTools: false, allowStorage: true).ShouldBeNull();

    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, """{"tools":[{"type":"function","function":{"name":"f"}},{"type":"web_search"}]}""", "web_search")]
    [InlineData(GatewayEndpoint.ChatCompletions, """{"web_search_options":{}}""", "web_search_options")]
    [InlineData(GatewayEndpoint.Responses, """{"tools":[{"type":"mcp","server_url":"https://mcp.example"}]}""", "mcp")]
    [InlineData(GatewayEndpoint.Responses, """{"tools":[{"type":"file_search","vector_store_ids":["vs_1"]}]}""", "file_search")]
    [InlineData(GatewayEndpoint.Responses, """{"tools":[{"type":"code_interpreter"}]}""", "code_interpreter")]
    [InlineData(GatewayEndpoint.Responses, """{"tools":[{"type":"web_search_preview"}]}""", "web_search_preview")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"tools":[{"type":"web_search_20250305","name":"web_search"}]}""", "web_search_20250305")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"tools":[{"type":"web_fetch_20250910","name":"web_fetch"}]}""", "web_fetch_20250910")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"tools":[{"type":"code_execution_20250825","name":"c"}]}""", "code_execution_20250825")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"mcp_servers":[{"type":"url","url":"https://mcp.example"}]}""", "mcp_servers")]
    [InlineData(GatewayEndpoint.AnthropicMessages, """{"container":"container_1"}""", "container")]
    public void Tools_the_provider_runs_are_refused_unless_allowed(GatewayEndpoint endpoint, string json, string feature)
    {
        ProviderFeaturePolicy.Apply(Body(json), endpoint, allowTools: false, allowStorage: true)
            .ShouldBe(new ProviderFeatureRefusal(Storage: false, feature));
        ProviderFeaturePolicy.Apply(Body(json), endpoint, allowTools: true, allowStorage: true).ShouldBeNull();
    }

    [Theory]
    [InlineData(GatewayEndpoint.ChatCompletions, """{"store":true}""", "store")]
    [InlineData(GatewayEndpoint.Responses, """{"store":true}""", "store")]
    [InlineData(GatewayEndpoint.Responses, """{"background":true}""", "background")]
    [InlineData(GatewayEndpoint.Responses, """{"conversation":"conv_1"}""", "conversation")]
    [InlineData(GatewayEndpoint.Responses, """{"conversation":{"id":"conv_1"}}""", "conversation")]
    public void Keeping_the_conversation_at_the_provider_is_refused_unless_allowed(GatewayEndpoint endpoint, string json, string feature)
    {
        ProviderFeaturePolicy.Apply(Body(json), endpoint, allowTools: true, allowStorage: false)
            .ShouldBe(new ProviderFeatureRefusal(Storage: true, feature));
        ProviderFeaturePolicy.Apply(Body(json), endpoint, allowTools: true, allowStorage: true).ShouldBeNull();
    }

    [Fact]
    public void Responses_requests_are_sent_with_store_false_unless_storage_is_allowed()
    {
        var body = Body("""{"model":"m","input":"Hej"}""");
        ProviderFeaturePolicy.Apply(body, GatewayEndpoint.Responses, allowTools: false, allowStorage: false).ShouldBeNull();
        body["store"]!.GetValue<bool>().ShouldBeFalse();

        var allowed = Body("""{"model":"m","input":"Hej"}""");
        ProviderFeaturePolicy.Apply(allowed, GatewayEndpoint.Responses, allowTools: false, allowStorage: true).ShouldBeNull();
        allowed.ContainsKey("store").ShouldBeFalse();

        // Chat Completions does not store unless asked, and some compatible servers refuse unknown fields: left as sent.
        var chat = Body("""{"model":"m","messages":[],"store":false}""");
        ProviderFeaturePolicy.Apply(chat, GatewayEndpoint.ChatCompletions, allowTools: false, allowStorage: false).ShouldBeNull();
        var omitted = Body("""{"model":"m","messages":[]}""");
        ProviderFeaturePolicy.Apply(omitted, GatewayEndpoint.ChatCompletions, allowTools: false, allowStorage: false).ShouldBeNull();
        omitted.ContainsKey("store").ShouldBeFalse();
    }

    [Fact]
    public void Other_endpoints_are_not_checked()
    {
        var body = Body("""{"model":"m","input":"x","store":true,"tools":[{"type":"web_search"}]}""");
        ProviderFeaturePolicy.Apply(body, GatewayEndpoint.Embeddings, allowTools: false, allowStorage: false).ShouldBeNull();
    }
}
