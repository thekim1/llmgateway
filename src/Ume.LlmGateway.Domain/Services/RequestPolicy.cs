using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Services;

/// <summary>
/// What an administrator imposes on a team's or a key's requests: a cap on output tokens, a system prompt placed
/// before the client's, parameter defaults (used when the client leaves a parameter out) and overrides (always used).
/// A key's values win over its team's (<see cref="Merge"/>). Configuration, not conversation content.
/// </summary>
public sealed record RequestPolicy
{
    public const int MaxSystemPromptLength = 8000;
    public const int MaxParameters = 20;

    /// <summary>Output tokens a request may ask for at most; a request that does not say gets this many.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Instructions placed before the client's system prompt or instructions.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Request parameters set when the client leaves them out (e.g. <c>temperature</c>, <c>reasoning_effort</c>).</summary>
    public IReadOnlyDictionary<string, JsonElement>? Defaults { get; init; }

    /// <summary>Request parameters always set, whatever the client sent.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Overrides { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => MaxOutputTokens is null && string.IsNullOrEmpty(SystemPrompt) && Defaults is not { Count: > 0 } && Overrides is not { Count: > 0 };

    /// <summary>
    /// Parameters a policy may not set: they carry the conversation or decide where and how it is sent, which the
    /// gateway controls (the model) or the client must (the content, streaming).
    /// </summary>
    public static IReadOnlySet<string> ProtectedParameters { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "model", "messages", "input", "prompt", "system", "instructions", "stream", "stream_options", "file", "image", "mask",
        "max_tokens", "max_completion_tokens", "max_output_tokens",
    };

    /// <summary>The team's policy with the key's values on top (parameters merged by name). Null when neither sets anything.</summary>
    public static RequestPolicy? Merge(RequestPolicy? team, RequestPolicy? key)
    {
        if (team is null || team.IsEmpty)
        {
            return key is null || key.IsEmpty ? null : key;
        }

        if (key is null || key.IsEmpty)
        {
            return team;
        }

        return new RequestPolicy
        {
            MaxOutputTokens = key.MaxOutputTokens ?? team.MaxOutputTokens,
            SystemPrompt = string.IsNullOrEmpty(key.SystemPrompt) ? team.SystemPrompt : key.SystemPrompt,
            Defaults = MergeParameters(team.Defaults, key.Defaults),
            Overrides = MergeParameters(team.Overrides, key.Overrides),
        };
    }

    /// <summary>Problems with the policy, worded for an administrator; empty when it is valid.</summary>
    public IEnumerable<string> Validate()
    {
        if (MaxOutputTokens is < 1 or > 1_000_000)
        {
            yield return "Max output tokens must be between 1 and 1,000,000.";
        }

        if (SystemPrompt?.Length > MaxSystemPromptLength)
        {
            yield return $"The system prompt can be at most {MaxSystemPromptLength} characters.";
        }

        foreach (var (label, parameters) in new[] { ("Defaults", Defaults), ("Overrides", Overrides) })
        {
            if (parameters is null)
            {
                continue;
            }

            if (parameters.Count > MaxParameters)
            {
                yield return $"{label} can set at most {MaxParameters} parameters.";
            }

            foreach (var name in parameters.Keys)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
                {
                    yield return $"{label}: parameter names must be 1–100 characters.";
                }
                else if (ProtectedParameters.Contains(name))
                {
                    yield return $"{label}: '{name}' cannot be set by a policy{(name.StartsWith("max_", StringComparison.Ordinal) ? "; use Max output tokens" : string.Empty)}.";
                }
            }
        }
    }

    private static Dictionary<string, JsonElement>? MergeParameters(IReadOnlyDictionary<string, JsonElement>? team, IReadOnlyDictionary<string, JsonElement>? key)
    {
        if (team is not { Count: > 0 })
        {
            return key is { Count: > 0 } ? new(key, StringComparer.Ordinal) : null;
        }

        var merged = new Dictionary<string, JsonElement>(team, StringComparer.Ordinal);
        foreach (var (name, value) in key ?? new Dictionary<string, JsonElement>())
        {
            merged[name] = value;
        }

        return merged;
    }
}

/// <summary>Applies a <see cref="RequestPolicy"/> to a request body, per endpoint.</summary>
public static class RequestPolicies
{
    /// <summary>
    /// Changes <paramref name="body"/> as the policy says. Text endpoints (chat completions, Responses, Messages) get
    /// all of it; token counting only the system prompt (so the count matches what will be sent); other JSON endpoints
    /// only defaults and overrides. Multipart uploads (audio, image edits) are left alone. Returns the characters of
    /// system prompt added, for the token estimate.
    /// </summary>
    public static int Apply(JsonObject body, GatewayEndpoint endpoint, RequestPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (policy is null || policy.IsEmpty)
        {
            return 0;
        }

        var info = GatewayEndpoints.Info(endpoint);
        if (info.IsAudio || info.IsImageUpload || info.IsRealtime)
        {
            return 0;
        }

        var text = endpoint is GatewayEndpoint.ChatCompletions or GatewayEndpoint.Responses or GatewayEndpoint.AnthropicMessages;
        var added = text || endpoint == GatewayEndpoint.AnthropicCountTokens ? AddSystemPrompt(body, endpoint, policy.SystemPrompt) : 0;
        if (endpoint == GatewayEndpoint.AnthropicCountTokens)
        {
            return added;
        }

        foreach (var (name, value) in policy.Defaults ?? new Dictionary<string, JsonElement>())
        {
            if (!RequestPolicy.ProtectedParameters.Contains(name) && body[name] is null)
            {
                body[name] = JsonNode.Parse(value.GetRawText());
            }
        }

        foreach (var (name, value) in policy.Overrides ?? new Dictionary<string, JsonElement>())
        {
            if (!RequestPolicy.ProtectedParameters.Contains(name))
            {
                body[name] = JsonNode.Parse(value.GetRawText());
            }
        }

        if (text && policy.MaxOutputTokens is { } cap)
        {
            CapOutputTokens(body, endpoint, cap);
        }

        return added;
    }

    /// <summary>Lowers every output-token parameter the client sent to <paramref name="cap"/>, or sets the endpoint's own one.</summary>
    private static void CapOutputTokens(JsonObject body, GatewayEndpoint endpoint, int cap)
    {
        var seen = false;
        foreach (var name in (string[])["max_tokens", "max_completion_tokens", "max_output_tokens"])
        {
            if (body[name] is JsonValue v && v.TryGetValue<long>(out var requested))
            {
                seen = true;
                if (requested > cap || requested <= 0)
                {
                    body[name] = cap;
                }
            }
        }

        if (!seen)
        {
            body[endpoint == GatewayEndpoint.Responses ? "max_output_tokens" : "max_tokens"] = cap;
        }
    }

    private static int AddSystemPrompt(JsonObject body, GatewayEndpoint endpoint, string? prompt)
    {
        if (string.IsNullOrEmpty(prompt))
        {
            return 0;
        }

        switch (endpoint)
        {
            case GatewayEndpoint.ChatCompletions:
                if (body["messages"] is not JsonArray messages)
                {
                    return 0;
                }

                messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = prompt });
                return prompt.Length;

            case GatewayEndpoint.Responses:
                body["instructions"] = Prepend(prompt, body["instructions"]);
                return prompt.Length;

            default: // Anthropic Messages and token counting: `system` is a string or an array of text blocks.
                if (body["system"] is JsonArray blocks)
                {
                    blocks.Insert(0, new JsonObject { ["type"] = "text", ["text"] = prompt });
                }
                else
                {
                    body["system"] = Prepend(prompt, body["system"]);
                }

                return prompt.Length;
        }
    }

    private static string Prepend(string prompt, JsonNode? existing) =>
        existing is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? prompt + "\n\n" + text : prompt;
}
