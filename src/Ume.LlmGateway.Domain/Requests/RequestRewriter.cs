using System.Text.Json.Nodes;

namespace Ume.LlmGateway.Domain.Requests;

/// <summary>
/// Adapts a client request body for a specific model deployment. The body is treated as an opaque
/// <see cref="JsonObject"/>: only the fields listed here are touched, everything else (new parameters
/// of future model families, tools, reasoning options…) passes through unchanged.
/// </summary>
public static class RequestRewriter
{
    private static readonly string[] ReasoningUnsupported = ["temperature", "top_p", "presence_penalty", "frequency_penalty", "logprobs", "top_logprobs", "logit_bias"];

    public static void Apply(JsonObject body, GatewayEndpoint endpoint, string upstreamModel, ParameterProfile profile, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(body);
        body["model"] = upstreamModel;

        if (endpoint == GatewayEndpoint.ChatCompletions)
        {
            if (profile == ParameterProfile.OpenAIReasoning)
            {
                if (body["max_tokens"] is { } maxTokens && body["max_completion_tokens"] is null)
                {
                    body.Remove("max_tokens");
                    body["max_completion_tokens"] = maxTokens.DeepClone();
                }

                foreach (var p in ReasoningUnsupported)
                {
                    body.Remove(p);
                }
            }

            // Ask OpenAI-compatible providers to include token usage in the final stream chunk so
            // streamed requests can be billed exactly.
            if (streaming)
            {
                var options = body["stream_options"] as JsonObject ?? [];
                options["include_usage"] = true;
                body["stream_options"] = options;
            }
        }
    }

    /// <summary>Upper bound of output tokens requested by the client, if any.</summary>
    public static long? RequestedMaxOutputTokens(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (var name in (string[])["max_completion_tokens", "max_tokens", "max_output_tokens"])
        {
            if (body[name] is JsonValue v && v.TryGetValue<long>(out var n) && n > 0)
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>Largest <c>n</c> taken into account for the budget estimate of an image request.</summary>
    public const int MaxEstimatedImages = 100;

    /// <summary>
    /// Pictures the client asked for (<c>n</c>, default 1). Multipart forms carry it as a string, JSON bodies as a number.
    /// Clamped to 1..<see cref="MaxEstimatedImages"/>: the provider refuses what it cannot make.
    /// </summary>
    public static int RequestedImageCount(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var n = body["n"] is JsonValue v && (v.TryGetValue<int>(out var number) || (v.TryGetValue<string>(out var text) && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)))
            ? number
            : 1;
        return Math.Clamp(n, 1, MaxEstimatedImages);
    }

    public static bool IsStreaming(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return body["stream"] is JsonValue v && v.TryGetValue<bool>(out var s) && s;
    }
}
