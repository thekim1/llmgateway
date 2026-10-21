using System.Globalization;
using System.Text.Json.Nodes;
using Ume.LlmGateway.Domain;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Gateway.Pipeline;

/// <summary>Stable, documented error codes returned by the gateway. See docs/admin-api.md#gateway-errors.</summary>
public static class GatewayErrorCodes
{
    public const string InvalidApiKey = "invalid_api_key";
    public const string KeyExpired = "key_expired";
    public const string KeyRevoked = "key_revoked";
    public const string KeyDisabled = "key_disabled";
    public const string AddressNotAllowed = "address_not_allowed";
    public const string InvalidRequest = "invalid_request";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string RequestTooLarge = "request_too_large";
    public const string ModelNotFound = "model_not_found";

    /// <summary>Several providers offer the plain model name and no route has it: the client must name the provider.</summary>
    public const string ModelAmbiguous = "model_ambiguous";
    public const string ModelNotAllowed = "model_not_allowed";
    public const string RateLimited = "rate_limited";
    public const string PiiBlocked = "pii_blocked";
    public const string AttachmentNotAllowed = "attachment_not_allowed";
    public const string NoEligibleProvider = "no_eligible_provider";
    public const string BudgetExceeded = "budget_exceeded";
    public const string AllProvidersFailed = "all_providers_failed";
    public const string ProviderRejected = "provider_rejected";

    /// <summary>Unexpected failure inside the gateway after the request was accepted (it is accounted; nothing is charged).</summary>
    public const string InternalError = "internal_error";
}

/// <summary>
/// A refusal as the client sees it: the HTTP status, a stable <see cref="GatewayErrorCodes">code</see> and a message.
/// Every message the gateway answers with is made here, so wording stays consistent. Live-session errors are sent as
/// Realtime <c>error</c> events, which carry the code and message only.
/// </summary>
public sealed record GatewayError(int StatusCode, string Code, string Message)
{
    // Authentication
    public static GatewayError InvalidApiKey { get; } = new(401, GatewayErrorCodes.InvalidApiKey,
        "The API key is missing or invalid. Send it as 'Authorization: Bearer ume-sk-…'.");
    public static GatewayError KeyRevoked { get; } = new(401, GatewayErrorCodes.KeyRevoked,
        "The API key has been revoked. Use its replacement or ask for a new key.");
    public static GatewayError KeyExpired { get; } = new(401, GatewayErrorCodes.KeyExpired,
        "The API key has expired. Ask your administrator to rotate it.");
    public static GatewayError KeyDisabled { get; } = new(403, GatewayErrorCodes.KeyDisabled, "The API key is temporarily disabled.");
    public static GatewayError KeyOwnerInactive { get; } = new(403, GatewayErrorCodes.KeyDisabled,
        "The team or department that owns the API key is deactivated.");
    public static GatewayError AddressNotAllowed { get; } = new(403, GatewayErrorCodes.AddressNotAllowed,
        "The API key may not be used from this address. Ask your administrator to add the address to the key.");

    // The request body
    public static GatewayError JsonRequired { get; } = new(415, GatewayErrorCodes.UnsupportedMediaType, "Content-Type must be application/json.");
    public static GatewayError RequestTooLarge { get; } = new(413, GatewayErrorCodes.RequestTooLarge, "The request is too large.");
    public static GatewayError InvalidJson { get; } = new(400, GatewayErrorCodes.InvalidRequest, "The request is not valid JSON.");
    public static GatewayError MissingModel { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "The 'model' field is missing. Give a model alias, for example 'ume/chat-standard'.");
    public static GatewayError ImageStreamingNotSupported { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "Streaming image responses (stream) are not supported. Send the request without 'stream'.");

    public static GatewayError AttachmentNotAllowed(AttachmentKinds refused, bool imagesOnly) => new(400, GatewayErrorCodes.AttachmentNotAllowed,
        $"The API key does not allow attached {AttachmentNames(refused)}{(imagesOnly ? " (only images are allowed)" : string.Empty)}. Send the request without files.");

    public static GatewayError PiiBlocked(string? categories) => new(400, GatewayErrorCodes.PiiBlocked,
        $"The request contains personal data ({categories}) and the key's policy does not allow it. Remove it and try again.");

    // Multipart uploads (audio and image edits)
    public static GatewayError MultipartRequired(string field, string what) => new(415, GatewayErrorCodes.UnsupportedMediaType,
        $"Content-Type must be multipart/form-data with the {what} in the '{field}' field.");
    public static GatewayError InvalidForm { get; } = new(400, GatewayErrorCodes.InvalidRequest, "The form is not valid multipart/form-data.");
    public static GatewayError FieldTooLarge(string name) => new(400, GatewayErrorCodes.InvalidRequest,
        $"The field '{(name.Length > 50 ? name[..50] : name)}' is too large.");
    public static GatewayError OneAudioFile { get; } = new(400, GatewayErrorCodes.InvalidRequest, "Send exactly one audio file, in the 'file' field.");
    public static GatewayError AudioFileMissing { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "The audio file is missing. Send it in the 'file' field (for example mp3, wav, m4a, ogg, webm or flac).");
    public static GatewayError AudioTooLarge(long maxBytes) => new(413, GatewayErrorCodes.RequestTooLarge,
        $"The audio file is too large (at most {maxBytes / (1024 * 1024)} MB). Split the recording or compress it, for example to mp3.");
    public static GatewayError ImageFields(int maxImages) => new(400, GatewayErrorCodes.InvalidRequest,
        $"Send the pictures in the 'image' field (at most {maxImages}) and an optional mask in the 'mask' field.");
    public static GatewayError ImageMissing { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "The picture is missing. Send it in the 'image' field (png, jpg or webp).");
    public static GatewayError ImagesTooLarge(long maxBytes) => new(413, GatewayErrorCodes.RequestTooLarge,
        $"The pictures are too large (at most {maxBytes / (1024 * 1024)} MB together). Reduce their resolution or compress them.");

    // Models and routing
    public static GatewayError ModelNotFound(string model) => new(404, GatewayErrorCodes.ModelNotFound,
        $"The model '{ModelNames.Truncate(model)}' does not exist. See GET /v1/models for the models you can use.");
    public static GatewayError ModelAmbiguous(string model, IReadOnlyList<string> choices) => new(400, GatewayErrorCodes.ModelAmbiguous,
        $"Several providers offer the model '{ModelNames.Truncate(model)}'. Name the provider: {string.Join(", ", choices)}. " +
        "An administrator can also create a route with this name to choose the provider or a fallback.");
    public static GatewayError ModelNotAllowed(string name) => new(403, GatewayErrorCodes.ModelNotAllowed, $"The API key may not use the model '{name}'.");
    public static GatewayError NoAllowedProvider(string name) => new(403, GatewayErrorCodes.ModelNotAllowed,
        $"The API key may not use any provider that offers '{name}'.");
    public static GatewayError EndpointNotSupported(string name) => new(400, GatewayErrorCodes.InvalidRequest,
        $"The model '{name}' cannot be used with this endpoint.");
    public static GatewayError RuleTargetsUnusable(string model) => new(503, GatewayErrorCodes.NoEligibleProvider,
        $"The routing rule for '{model}' points to models that do not exist or cannot be used with this endpoint.");
    public static GatewayError NoEligibleProvider(bool onPremOnly) => new(503, GatewayErrorCodes.NoEligibleProvider, onPremOnly
        ? "The request contains personal data and may only be sent to a local (on-prem) model, but this alias has none."
        : "No available provider can handle the request for this alias within the key's restrictions.");

    // Limits and budgets
    public static GatewayError RateLimited(bool tokens, int retryAfterSeconds) => new(429, GatewayErrorCodes.RateLimited,
        $"The key has reached its limit of {(tokens ? "tokens" : "requests")} per minute. Try again in {retryAfterSeconds} s.");
    public static GatewayError TooManyAuthFailures(int retryAfterSeconds) => new(429, GatewayErrorCodes.RateLimited,
        $"Too many requests with a missing or invalid API key from this address. Try again in {retryAfterSeconds} s.");
    public static GatewayError TooManySessions(int max) => new(429, GatewayErrorCodes.RateLimited,
        $"The key already has {max} open realtime sessions. Close one and try again.");

    /// <summary>A budget is used up; <paramref name="action"/> says what happens next.</summary>
    public static GatewayError BudgetExceeded(Budget budget, string action)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var scope = budget.Scope switch
        {
            BudgetScope.Department => "The department's",
            BudgetScope.Team => "The team's",
            _ => "The key's",
        };
        var limit = budget.LimitUsd.ToString("0.##", CultureInfo.InvariantCulture);
        return new(402, GatewayErrorCodes.BudgetExceeded, $"{scope} budget (USD {limit} per {PeriodName(budget.Period)}) is used up. {action}");
    }

    // Providers and the gateway
    public static GatewayError AllProvidersFailed(bool timedOut, int candidates, string model, string? lastReason) => new(timedOut ? 504 : 502,
        GatewayErrorCodes.AllProvidersFailed, $"All {candidates} providers for '{model}' failed. Last error: {lastReason ?? "unknown"}.");
    public static GatewayError InternalError { get; } = new(500, GatewayErrorCodes.InternalError,
        "An internal error occurred in the gateway. Try again; if it persists, contact your administrator and give the request id.");

    // Live sessions (WebSocket)
    public static GatewayError WebSocketRequired { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "This endpoint needs a WebSocket connection: wss://…/v1/realtime?model=<alias>.");
    public static GatewayError SessionModelMissing { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "The 'model' parameter is missing. Connect with ?model=<alias>, for example 'ume/live-transcribe'.");
    public static GatewayError SessionAudioNotAllowed(AttachmentKinds refused) => new(400, GatewayErrorCodes.AttachmentNotAllowed,
        $"The API key does not allow {AttachmentNames(refused)}, and a realtime session sends audio.");
    public static GatewayError SessionRejectedByProvider(int status) => new(status, GatewayErrorCodes.ProviderRejected,
        $"The provider rejected the session ({status}). Check the parameters in the connection URL.");
    public static GatewayError SessionTimeLimit(int minutes) => new(400, "session_time_limit",
        $"The session has reached its maximum length of {minutes} minutes. Connect again to continue.");
    public static GatewayError GatewayShuttingDown { get; } = new(503, "gateway_shutting_down", "The gateway is restarting. Connect again to continue.");
    public static GatewayError ProviderDisconnected { get; } = new(502, "provider_disconnected", "The connection to the provider was lost.");
    public static GatewayError EventTooLarge(int maxBytes) => new(413, GatewayErrorCodes.RequestTooLarge,
        $"The event is larger than {maxBytes / 1024} kB. Send the audio in smaller chunks.");
    public static GatewayError BinaryMessagesNotSupported { get; } = new(400, GatewayErrorCodes.InvalidRequest,
        "Binary messages are not supported. Send audio as base64 in input_audio_buffer.append.");
    public static GatewayError EventPiiBlocked(string? categories) => new(400, GatewayErrorCodes.PiiBlocked,
        $"The event contains personal data ({categories}) and the key's policy does not allow it. It was not forwarded.");
    public static GatewayError EventPiiNeedsOnPrem(string? categories) => new(400, GatewayErrorCodes.PiiBlocked,
        $"The event contains personal data ({categories}) that may only go to a local (on-prem) model, but the session goes to an external provider. It was not forwarded.");
    public static GatewayError TranscriptionModelNotAllowed(string model) => new(403, GatewayErrorCodes.ModelNotAllowed,
        $"The transcription model '{ModelNames.Truncate(model)}' is not offered by the session's provider or may not be used with the key. Give a speech-to-text alias from GET /v1/models.");

    private static readonly (AttachmentKinds Kind, string Name)[] AttachmentKindNames =
        [(AttachmentKinds.Image, "images"), (AttachmentKinds.Document, "documents"), (AttachmentKinds.Audio, "audio files"), (AttachmentKinds.Video, "video files")];

    private static string AttachmentNames(AttachmentKinds kinds) =>
        string.Join(" or ", AttachmentKindNames.Where(n => kinds.HasFlag(n.Kind)).Select(n => n.Name));

    private static string PeriodName(BudgetPeriod period) => period switch
    {
        BudgetPeriod.Hourly => "hour",
        BudgetPeriod.Daily => "day",
        BudgetPeriod.Weekly => "week",
        BudgetPeriod.Monthly => "month",
        BudgetPeriod.Quarterly => "quarter",
        _ => "year",
    };
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

    public static Task WriteAsync(HttpContext http, GatewayEndpoint endpoint, GatewayError error, string docsUrl)
    {
        ArgumentNullException.ThrowIfNull(error);
        return WriteAsync(http, endpoint, error.StatusCode, error.Code, error.Message, docsUrl);
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
