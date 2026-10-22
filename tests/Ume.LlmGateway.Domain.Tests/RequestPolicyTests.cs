using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain.Services;

namespace Ume.LlmGateway.Domain.Tests;

public sealed class RequestPolicyTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly RequestPolicy Policy = new()
    {
        MaxOutputTokens = 500,
        SystemPrompt = "Svara på svenska.",
        Defaults = new Dictionary<string, JsonElement> { ["temperature"] = Json("0.2"), ["reasoning_effort"] = Json("\"low\"") },
        Overrides = new Dictionary<string, JsonElement> { ["user"] = Json("\"municipal\"") },
    };

    [Fact]
    public void Chat_gets_the_system_prompt_first_a_capped_output_and_parameters()
    {
        var body = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"Hej"}],"max_tokens":4000,"temperature":0.9,"user":"client"}""")!.AsObject();
        RequestPolicies.Apply(body, GatewayEndpoint.ChatCompletions, Policy).ShouldBe(Policy.SystemPrompt!.Length);
        body["messages"]![0]!["role"]!.GetValue<string>().ShouldBe("system");
        body["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Svara på svenska.");
        body["messages"]![1]!["content"]!.GetValue<string>().ShouldBe("Hej");
        body["max_tokens"]!.GetValue<int>().ShouldBe(500);
        body["temperature"]!.GetValue<double>().ShouldBe(0.9); // a default does not replace what the client sent
        body["reasoning_effort"]!.GetValue<string>().ShouldBe("low");
        body["user"]!.GetValue<string>().ShouldBe("municipal"); // an override does
    }

    [Fact]
    public void Output_cap_is_set_when_the_client_asks_for_none_and_kept_when_it_asks_for_less()
    {
        var none = JsonNode.Parse("""{"model":"m","input":"Hej"}""")!.AsObject();
        RequestPolicies.Apply(none, GatewayEndpoint.Responses, new RequestPolicy { MaxOutputTokens = 300 });
        none["max_output_tokens"]!.GetValue<int>().ShouldBe(300);

        var less = JsonNode.Parse("""{"model":"m","messages":[],"max_completion_tokens":100}""")!.AsObject();
        RequestPolicies.Apply(less, GatewayEndpoint.ChatCompletions, new RequestPolicy { MaxOutputTokens = 300 });
        less["max_completion_tokens"]!.GetValue<int>().ShouldBe(100);
        less["max_tokens"].ShouldBeNull();
    }

    [Fact]
    public void Responses_and_messages_prepend_to_their_own_instructions()
    {
        var responses = JsonNode.Parse("""{"model":"m","input":"Hej","instructions":"Be brief."}""")!.AsObject();
        RequestPolicies.Apply(responses, GatewayEndpoint.Responses, Policy);
        responses["instructions"]!.GetValue<string>().ShouldBe("Svara på svenska.\n\nBe brief.");

        var text = JsonNode.Parse("""{"model":"m","system":"Be brief.","messages":[],"max_tokens":50}""")!.AsObject();
        RequestPolicies.Apply(text, GatewayEndpoint.AnthropicMessages, Policy);
        text["system"]!.GetValue<string>().ShouldBe("Svara på svenska.\n\nBe brief.");

        var blocks = JsonNode.Parse("""{"model":"m","system":[{"type":"text","text":"Be brief.","cache_control":{"type":"ephemeral"}}],"messages":[]}""")!.AsObject();
        RequestPolicies.Apply(blocks, GatewayEndpoint.AnthropicMessages, Policy);
        blocks["system"]!.AsArray().Select(b => b!["text"]!.GetValue<string>()).ShouldBe(["Svara på svenska.", "Be brief."]);
        blocks["system"]![1]!["cache_control"].ShouldNotBeNull(); // the client's cache marker stays where it was
    }

    [Fact]
    public void Token_counting_gets_only_the_prompt_and_embeddings_only_parameters()
    {
        var count = JsonNode.Parse("""{"model":"m","messages":[]}""")!.AsObject();
        RequestPolicies.Apply(count, GatewayEndpoint.AnthropicCountTokens, Policy);
        count["system"]!.GetValue<string>().ShouldBe("Svara på svenska.");
        count["max_tokens"].ShouldBeNull();
        count["temperature"].ShouldBeNull();

        var embedding = JsonNode.Parse("""{"model":"m","input":"Hej"}""")!.AsObject();
        RequestPolicies.Apply(embedding, GatewayEndpoint.Embeddings, Policy).ShouldBe(0);
        embedding["user"]!.GetValue<string>().ShouldBe("municipal");
        embedding["max_tokens"].ShouldBeNull();
        embedding["instructions"].ShouldBeNull();
    }

    [Fact]
    public void Key_values_win_over_the_team_and_parameters_merge_by_name()
    {
        var team = new RequestPolicy
        {
            MaxOutputTokens = 1000, SystemPrompt = "Team prompt",
            Defaults = new Dictionary<string, JsonElement> { ["temperature"] = Json("0.2"), ["top_p"] = Json("0.9") },
        };
        var key = new RequestPolicy { MaxOutputTokens = 200, Defaults = new Dictionary<string, JsonElement> { ["temperature"] = Json("0") } };

        var merged = RequestPolicy.Merge(team, key)!;
        merged.MaxOutputTokens.ShouldBe(200);
        merged.SystemPrompt.ShouldBe("Team prompt");
        merged.Defaults!["temperature"].GetDouble().ShouldBe(0);
        merged.Defaults["top_p"].GetDouble().ShouldBe(0.9);

        RequestPolicy.Merge(null, null).ShouldBeNull();
        RequestPolicy.Merge(new RequestPolicy(), new RequestPolicy()).ShouldBeNull();
        RequestPolicy.Merge(team, null).ShouldBeSameAs(team);
    }

    [Fact]
    public void Policies_cannot_touch_the_conversation_or_the_model()
    {
        var policy = new RequestPolicy
        {
            MaxOutputTokens = 0,
            SystemPrompt = new string('x', RequestPolicy.MaxSystemPromptLength + 1),
            Overrides = new Dictionary<string, JsonElement> { ["model"] = Json("\"gpt\""), ["max_tokens"] = Json("5") },
        };
        var problems = policy.Validate().ToList();
        problems.Count.ShouldBe(4);
        problems.ShouldContain(p => p.Contains("'model'", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("Max output tokens", StringComparison.Ordinal) && p.Contains("'max_tokens'", StringComparison.Ordinal));

        // Provider tools and storage are the key's own permission, checked before the policy: a policy cannot add them.
        var sneaky = new RequestPolicy
        {
            Overrides = new Dictionary<string, JsonElement> { ["tools"] = Json("""[{"type":"web_search"}]"""), ["store"] = Json("true") },
        };
        sneaky.Validate().Count().ShouldBe(2);
        var request = JsonNode.Parse("""{"model":"m","input":"Hej","store":false}""")!.AsObject();
        RequestPolicies.Apply(request, GatewayEndpoint.Responses, sneaky);
        request["tools"].ShouldBeNull();
        request["store"]!.GetValue<bool>().ShouldBeFalse();

        // Even if a stored policy holds one, it is not applied.
        var body = JsonNode.Parse("""{"model":"chosen","messages":[]}""")!.AsObject();
        RequestPolicies.Apply(body, GatewayEndpoint.ChatCompletions, new RequestPolicy { Overrides = policy.Overrides });
        body["model"]!.GetValue<string>().ShouldBe("chosen");
    }
}
