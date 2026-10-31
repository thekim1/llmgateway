using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// What a client sees when a provider refuses its request (a 4xx that another provider would refuse too): the provider's
/// message, type, code and param in the client API's error shape. Everything else a provider puts in its error body (inner
/// errors, deployment names, organisation ids, regions) stays out. The body is not logged: providers may quote the request.
/// </summary>
public static class ProviderErrors
{
    public const string DefaultMessage = "The provider rejected the request.";
    private const int MaxFieldLength = 1000;

    public static string ClientBody(GatewayEndpoint endpoint, string? providerBody, string requestId)
    {
        string? message = null, type = null, code = null, param = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(providerBody) && JsonNode.Parse(providerBody) is JsonObject root)
            {
                // OpenAI and Anthropic nest the error; some on-premises servers put the fields at the top.
                var error = root["error"] as JsonObject ?? root;
                message = Text(error["message"]);
                type = Text(error["type"]);
                code = Text(error["code"]);
                param = Text(error["param"]);
            }
        }
        catch (JsonException)
        {
            // Not JSON (an HTML error page from a proxy, say): only the default message is passed on.
        }

        if (endpoint == GatewayEndpoint.AnthropicMessages)
        {
            return new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = type ?? "invalid_request_error", ["message"] = message ?? DefaultMessage },
                ["request_id"] = requestId,
            }.ToJsonString();
        }

        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message ?? DefaultMessage,
                ["type"] = type ?? "invalid_request_error",
                ["code"] = code ?? GatewayErrorCodes.ProviderRejected,
                ["param"] = param,
                ["request_id"] = requestId,
            },
        }.ToJsonString();
    }

    /// <summary>A string or number field, without control characters and at most <see cref="MaxFieldLength"/> characters.</summary>
    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        var text = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sb = new StringBuilder(Math.Min(text.Length, MaxFieldLength));
        foreach (var c in text)
        {
            if (sb.Length == MaxFieldLength)
            {
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
