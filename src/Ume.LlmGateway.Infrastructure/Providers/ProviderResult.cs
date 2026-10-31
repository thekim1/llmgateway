using System.Buffers;
using Ume.LlmGateway.Domain.Usage;

namespace Ume.LlmGateway.Infrastructure.Providers;

public abstract class ProviderResult;

/// <summary>Why a provider attempt failed, which decides what the gateway does next (see <see cref="ProviderFailure"/>).</summary>
public enum FailureKind
{
    /// <summary>Overloaded, unreachable, timed out or answered garbage: try the next provider and count it against the circuit breaker.</summary>
    Transient = 0,

    /// <summary>Our configuration is wrong (upstream 401/403/404, plain HTTP in production, undecryptable credential): try the next provider.</summary>
    ProviderConfig = 1,

    /// <summary>The client's request is the problem (e.g. an invalid parameter): answer it, another provider would refuse it too.</summary>
    ClientError = 2,

    /// <summary>The provider type cannot serve this endpoint at all: try the next provider.</summary>
    Unsupported = 3,
}

/// <summary>Upstream failed before any byte was sent to the client. <see cref="Kind"/> decides fallback and circuit breaking.</summary>
public sealed class ProviderFailure(int statusCode, FailureKind kind, string reason, string? body = null, string? contentType = null, TimeSpan? retryAfter = null) : ProviderResult
{
    /// <summary>Production refuses to send content or credentials over plain HTTP.</summary>
    public static ProviderFailure HttpsRequired { get; } = new(503, FailureKind.ProviderConfig, "https_required");

    /// <summary>The stored credential cannot be decrypted (Data Protection key ring missing?).</summary>
    public static ProviderFailure CredentialUnavailable { get; } = new(500, FailureKind.ProviderConfig, "credential_unavailable");

    public static ProviderFailure EndpointNotSupported { get; } = new(400, FailureKind.Unsupported, "endpoint_not_supported");

    public static ProviderFailure Timeout { get; } = new(504, FailureKind.Transient, "timeout");

    public static ProviderFailure InvalidJson { get; } = new(502, FailureKind.Transient, "invalid_json");

    public int StatusCode { get; } = statusCode;
    public FailureKind Kind { get; } = kind;
    public string Reason { get; } = reason;
    public string? Body { get; } = body;
    public string? ContentType { get; } = contentType;

    /// <summary>How long the provider asked us to wait (<c>Retry-After</c>, <c>retry-after-ms</c>); null when it did not say.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Everything but a client error is worth another provider.</summary>
    public bool CanFallBack => Kind != FailureKind.ClientError;

    /// <summary>
    /// A quick, passing failure worth one more try on the same deployment: a connection error or 502/503/504 without
    /// <c>Retry-After</c>. Not a timeout (the attempt already took the whole timeout and may still be generating), not
    /// a 429 or a <c>Retry-After</c> (a quota: the deployment is put on cooldown instead).
    /// </summary>
    public bool IsRetryable => Kind == FailureKind.Transient && RetryAfter is null
        && (Reason.StartsWith("connection_error", StringComparison.Ordinal) || StatusCode is 502 or 503 && Reason.StartsWith("http_", StringComparison.Ordinal) || Reason == "http_504");

    /// <summary>Only transient failures say something about the provider's health.</summary>
    public bool CountsAgainstCircuit => Kind == FailureKind.Transient;

    /// <summary>What an upstream HTTP error status means.</summary>
    public static FailureKind KindForStatus(int status) => status switch
    {
        408 or 409 or 429 or >= 500 => FailureKind.Transient,
        401 or 403 or 404 => FailureKind.ProviderConfig,
        _ => FailureKind.ClientError,
    };

    /// <summary>A failure for an upstream HTTP error status, with the provider's error body when there is one.</summary>
    public static ProviderFailure FromStatus(int status, string? body = null, string? contentType = null, TimeSpan? retryAfter = null) =>
        new(status, KindForStatus(status), $"http_{status}", body, contentType, retryAfter);
}

/// <summary>
/// A complete JSON answer, held as the UTF-8 bytes to send to the client so it is never re-serialised. When
/// <c>pooled</c> is given, <see cref="Body"/> lives in that rented array and <see cref="Dispose"/> returns it.
/// Transcriptions may also be plain text, SRT or WebVTT; <see cref="ContentType"/> is then the provider's.
/// </summary>
public sealed class ProviderJsonResult(int statusCode, ReadOnlyMemory<byte> body, TokenUsage usage, byte[]? pooled = null, string? contentType = null) : ProviderResult, IDisposable
{
    private byte[]? _pooled = pooled;

    public int StatusCode { get; } = statusCode;
    public ReadOnlyMemory<byte> Body { get; } = body;
    public TokenUsage Usage { get; } = usage;

    /// <summary>Null means <c>application/json</c>.</summary>
    public string? ContentType { get; } = contentType;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pooled, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Streaming response. Usage is populated while <see cref="Events"/> is enumerated.</summary>
public sealed class ProviderStreamResult(IAsyncEnumerable<SseEvent> events, UsageAccumulator usage, IDisposable? owner) : ProviderResult, IDisposable
{
    public IAsyncEnumerable<SseEvent> Events { get; } = events;
    public UsageAccumulator Usage { get; } = usage;

    public void Dispose() => owner?.Dispose();
}
