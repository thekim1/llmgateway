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

    public static string ReasonName(AuthFailureReason reason) => reason switch
    {
        AuthFailureReason.MissingKey => "missing_key",
        AuthFailureReason.InvalidKey => "invalid_key",
        AuthFailureReason.KeyRevoked => "key_revoked",
        AuthFailureReason.KeyExpired => "key_expired",
        AuthFailureReason.KeyDisabled => "key_disabled",
        AuthFailureReason.OwnerInactive => "owner_inactive",
        _ => "unknown",
    };

    [LoggerMessage(EventId = 1001, EventName = "gateway.auth.failed", Level = LogLevel.Warning,
        Message = "Authentication failed: {Reason} on {Endpoint} x{Count} from {SourceAddress} (key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}) between {FirstSeen} and {LastSeen}")]
    public static partial void AuthFailed(ILogger logger, string reason, string endpoint, int count, string? sourceAddress,
        Guid? keyId, string? keyPrefix, Guid? teamId, Guid? departmentId, DateTimeOffset firstSeen, DateTimeOffset lastSeen);

    [LoggerMessage(EventId = 1002, EventName = "gateway.pii.action", Level = LogLevel.Information,
        Message = "Personal data {Action} in request {RequestId} on {Endpoint} ({Categories}); key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}")]
    public static partial void PiiAction(ILogger logger, string action, string requestId, string endpoint, string? categories,
        Guid keyId, string keyPrefix, Guid teamId, Guid departmentId);

    [LoggerMessage(EventId = 1003, EventName = "gateway.request.refused", Level = LogLevel.Information,
        Message = "Request {RequestId} on {Endpoint} refused by policy: {ErrorCode}; key {KeyId} {KeyPrefix}, team {TeamId}, department {DepartmentId}")]
    public static partial void RequestRefused(ILogger logger, string requestId, string endpoint, string errorCode,
        Guid keyId, string keyPrefix, Guid teamId, Guid departmentId);

    [LoggerMessage(EventId = 3001, EventName = "data.read", Level = LogLevel.Information,
        Message = "Data API client {Client} read {Path}: status {Status}, next cursor {Cursor}")]
    public static partial void DataRead(ILogger logger, string client, string path, int status, string? cursor);

    [LoggerMessage(EventId = 2001, EventName = "admin.change", Level = LogLevel.Information,
        Message = "Admin {Actor}: {Action} {EntityType} {EntityId}")]
    public static partial void AdminChange(ILogger logger, string actor, string action, string entityType, string? entityId);
}
