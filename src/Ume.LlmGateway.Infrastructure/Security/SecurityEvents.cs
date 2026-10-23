using System.Buffers;
using Microsoft.Extensions.Logging;
using Ume.LlmGateway.Domain;

namespace Ume.LlmGateway.Infrastructure.Security;

/// <summary>
/// Security events for SIEMs (see docs/data-access.md). Everything is logged under one category so an OpenTelemetry
/// Collector can route it separately from operational logs. Event names and attribute names are a public contract:
/// add, never rename or remove. Never log key material, prompt content or audit details here.
/// </summary>
public static partial class SecurityEvents
{
    public const string Category = "Ume.LlmGateway.Security";

    public static ILogger CreateLogger(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return factory.CreateLogger(Category);
    }

    private static readonly SearchValues<char> ControlCharacters = SearchValues.Create(
        [.. Enumerable.Range(0, 0x20).Select(c => (char)c), .. Enumerable.Range(0x7F, 0x21).Select(c => (char)c)]);

    /// <summary>
    /// <paramref name="value"/> with control characters (line breaks, escape sequences) replaced by '?', for client-supplied
    /// values that are logged or stored: a newline could otherwise start a forged log line in a plain-text log. Returns the
    /// same string, without allocating, when there is nothing to replace.
    /// </summary>
    public static string Printable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var first = value.AsSpan().IndexOfAny(ControlCharacters);
        if (first < 0)
        {
            return value;
        }

        return string.Create(value.Length, (value, first), static (span, state) =>
        {
            state.value.AsSpan().CopyTo(span);
            var rest = span[state.first..];
            for (var i = rest.IndexOfAny(ControlCharacters); i >= 0; i = rest.IndexOfAny(ControlCharacters))
            {
                rest[i] = '?';
                rest = rest[(i + 1)..];
            }
        });
    }

    public static string ReasonName(AuthFailureReason reason) => reason switch
    {
        AuthFailureReason.MissingKey => "missing_key",
        AuthFailureReason.InvalidKey => "invalid_key",
        AuthFailureReason.KeyRevoked => "key_revoked",
        AuthFailureReason.KeyExpired => "key_expired",
        AuthFailureReason.KeyDisabled => "key_disabled",
        AuthFailureReason.OwnerInactive => "owner_inactive",
        AuthFailureReason.AddressNotAllowed => "address_not_allowed",
        _ => "unknown",
    };

    // Event id and level are constructor arguments: CA1873 reads the level only from there to recognise IsEnabled guards.
    [LoggerMessage(1001, LogLevel.Warning,
        "Authentication failed: {Reason} on {Endpoint} x{Count} from {SourceAddress} (key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}) between {FirstSeen} and {LastSeen}", EventName = "gateway.auth.failed")]
    public static partial void AuthFailed(ILogger logger, string reason, string endpoint, int count, string? sourceAddress,
        Guid? keyId, string? keyPrefix, Guid? teamId, Guid? departmentId, DateTimeOffset firstSeen, DateTimeOffset lastSeen);

    [LoggerMessage(1002, LogLevel.Information,
        "Personal data {Action} in request {RequestId} on {Endpoint} ({Categories}); key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}", EventName = "gateway.pii.action")]
    public static partial void PiiAction(ILogger logger, string action, string requestId, string endpoint, string? categories,
        Guid keyId, string keyPrefix, Guid teamId, Guid departmentId);

    [LoggerMessage(1003, LogLevel.Information,
        "Request {RequestId} on {Endpoint} refused by policy: {ErrorCode}; key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}", EventName = "gateway.request.refused")]
    public static partial void RequestRefused(ILogger logger, string requestId, string endpoint, string errorCode,
        Guid keyId, string keyPrefix, Guid teamId, Guid departmentId);

    [LoggerMessage(1005, LogLevel.Information,
        "Content guard {Action} {Part} of request {RequestId} on {Endpoint} ({Flags}); key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}", EventName = "gateway.content.flagged")]
    public static partial void ContentFlagged(ILogger logger, string action, string part, string requestId, string endpoint, string flags,
        Guid keyId, string keyPrefix, Guid teamId, Guid departmentId);

    [LoggerMessage(3001, LogLevel.Information,
        "Data API client {Client} read {Path}: status {Status}, next cursor {Cursor}", EventName = "data.read")]
    public static partial void DataRead(ILogger logger, string client, string path, int status, string? cursor);

    [LoggerMessage(3002, LogLevel.Warning,
        "Data API key refused: {Reason} (key {KeyId} {KeyPrefix})", EventName = "data.auth.failed")]
    public static partial void DataKeyRefused(ILogger logger, string reason, Guid keyId, string keyPrefix);

    [LoggerMessage(2001, LogLevel.Information,
        "Admin {Actor}: {Action} {EntityType} {EntityId}", EventName = "admin.change")]
    public static partial void AdminChange(ILogger logger, string actor, string action, string entityType, string? entityId);

    [LoggerMessage(2002, LogLevel.Information,
        "Admin {Actor} signed in from {SourceAddress} with roles {Roles}, departments {Departments}; session {SessionId}", EventName = "admin.signin")]
    public static partial void AdminSignIn(ILogger logger, string actor, string? sourceAddress, string roles, string departments, string? sessionId);

    [LoggerMessage(2003, LogLevel.Information,
        "Admin {Actor} signed out from {SourceAddress}; session {SessionId}", EventName = "admin.signout")]
    public static partial void AdminSignOut(ILogger logger, string actor, string? sourceAddress, string? sessionId);

    [LoggerMessage(2004, LogLevel.Information,
        "Admin {Actor} extended session {SessionId} until {ExpiresAt} from {SourceAddress}", EventName = "admin.session.extended")]
    public static partial void AdminSessionExtended(ILogger logger, string actor, string? sessionId, DateTimeOffset expiresAt, string? sourceAddress);

    [LoggerMessage(2005, LogLevel.Warning,
        "Admin {Actor} was refused {Method} {Path} x{Count} from {SourceAddress} (roles {Roles})", EventName = "admin.access.denied")]
    public static partial void AdminAccessDenied(ILogger logger, string actor, string method, string path, int count, string? sourceAddress, string roles);

    [LoggerMessage(2006, LogLevel.Information,
        "Admin {Actor} read {Path}{Query} from {SourceAddress}", EventName = "admin.data.read")]
    public static partial void AdminDataRead(ILogger logger, string actor, string path, string query, string? sourceAddress);
}
