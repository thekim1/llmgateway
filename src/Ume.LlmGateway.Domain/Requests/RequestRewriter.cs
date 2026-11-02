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

    /// <summary>Every top-level field <see cref="Apply"/> may set, remove or change; <see cref="Capture"/> saves these.</summary>
    private static readonly string[] Rewritten = ["model", "max_tokens", "max_completion_tokens", "stream_options", .. ReasoningUnsupported];

    /// <summary>
    /// Saves the client's values of the fields <see cref="Apply"/> rewrites, so <see cref="Restore"/> can undo a rewrite
    /// before the body is rewritten for another deployment. Only these few small values are copied, not the body (which can
    /// hold megabytes of inline images).
    /// </summary>
    public static RewriteSnapshot Capture(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var values = new JsonNode?[Rewritten.Length];
        var present = new bool[Rewritten.Length];
        for (var i = 0; i < Rewritten.Length; i++)
        {
            if (body.TryGetPropertyValue(Rewritten[i], out var value))
            {
                present[i] = true;
                values[i] = value?.DeepClone();
            }
        }

        return new RewriteSnapshot(values, present);
    }

    /// <summary>Puts back the values <see cref="Capture"/> saved; the body is then as the client sent it.</summary>
    public static void Restore(JsonObject body, RewriteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(snapshot);
        for (var i = 0; i < Rewritten.Length; i++)
        {
            if (snapshot.Present[i])
            {
                // A copy: the saved value stays unparented for the next restore.
                body[Rewritten[i]] = snapshot.Values[i]?.DeepClone();
            }
            else
            {
                body.Remove(Rewritten[i]);
            }
        }
    }

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

/// <summary>The client's values of the fields <see cref="RequestRewriter.Apply"/> rewrites (see <see cref="RequestRewriter.Capture"/>).</summary>
public sealed class RewriteSnapshot
{
    internal RewriteSnapshot(JsonNode?[] values, bool[] present)
    {
        Values = values;
        Present = present;
    }

    internal JsonNode?[] Values { get; }

    internal bool[] Present { get; }
}
