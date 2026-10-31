using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ume.LlmGateway.AdminApi.Auditing;

/// <summary>The <c>Details</c> of an audit entry.</summary>
public sealed record AuditDetails(object? Before, object? After);

internal static class AuditJson
{
    /// <summary>camelCase and enum names, like the API's own responses.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}
