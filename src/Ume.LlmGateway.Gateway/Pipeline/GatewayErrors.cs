using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Stable, documented error codes returned by the gateway. See docs/admin-api.md#gateway-errors.</summary>
public static class GatewayErrorCodes
{
    public const string InvalidApiKey = "invalid_api_key";
    public const string KeyExpired = "key_expired";
    public const string KeyRevoked = "key_revoked";
    public const string KeyDisabled = "key_disabled";
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string RequestTooLarge = "request_too_large";
    public const string ModelNotFound = "model_not_found";
    public const string ModelNotAllowed = "model_not_allowed";
    public const string RateLimited = "rate_limited";
    public const string PiiBlocked = "pii_blocked";
    public const string AttachmentNotAllowed = "attachment_not_allowed";
    public const string NoEligibleProvider = "no_eligible_provider";
    public const string BudgetExceeded = "budget_exceeded";
    public const string AllProvidersFailed = "all_providers_failed";
    public const string ProviderRejected = "provider_rejected";
}

public static class GatewayErrors
{
    /// <summary>OpenAI-compatible error body, or Anthropic-compatible for <c>/v1/messages</c>.</summary>
    public static JsonObject Create(GatewayEndpoint endpoint, int status, string code, string message, string requestId, string docsUrl)
    {
        var type = TypeFor(status, endpoint);
        if (endpoint == GatewayEndpoint.AnthropicMessages)
        {
            return new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = type, ["message"] = message, ["code"] = code },
                ["request_id"] = requestId,
            };
        }

        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = type,
                ["code"] = code,
                ["param"] = null,
                ["request_id"] = requestId,
                ["doc_url"] = $"{docsUrl.TrimEnd('/')}#{code}",
            },
        };
    }

    public static async Task WriteAsync(HttpContext http, GatewayEndpoint endpoint, int status, string code, string message, string docsUrl)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Response.HasStarted)
        {
            return;
        }

        var requestId = RequestIdMiddleware.Get(http);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        await http.Response.WriteAsync(Create(endpoint, status, code, message, requestId, docsUrl).ToJsonString(), http.RequestAborted);
    }

    private static string TypeFor(int status, GatewayEndpoint endpoint) => status switch
    {
        400 or 413 or 415 => "invalid_request_error",
        401 => "authentication_error",
        402 => endpoint == GatewayEndpoint.AnthropicMessages ? "permission_error" : "insufficient_quota",
        403 => "permission_error",
        404 => "not_found_error",
        429 => "rate_limit_error",
        503 => endpoint == GatewayEndpoint.AnthropicMessages ? "overloaded_error" : "service_unavailable",
        _ => "api_error",
    };
}

/// <summary>Assigns a server-generated, unguessable request id (never trusted from the client).</summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "x-request-id";
    private const string ItemKey = "ume.request-id";

    public static string Get(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Items[ItemKey] is string id)
        {
            return id;
        }

        id = "req_" + Guid.CreateVersion7().ToString("N");
        http.Items[ItemKey] = id;
        return id;
    }

    public Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var id = Get(http);
        System.Diagnostics.Activity.Current?.SetTag("ume.request_id", id);
        http.Response.Headers[HeaderName] = id;
        return next(http);
    }
}
