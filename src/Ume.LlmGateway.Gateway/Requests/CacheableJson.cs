using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Net.Http.Headers;

namespace Ume.LlmGateway.Gateway.Requests;

/// <summary>
/// Answers of the discovery endpoints (<c>/v1/models</c>, <c>/v1/ume/context</c>) with an <c>ETag</c> over the body, so a
/// client that polls sends <c>If-None-Match</c> and usually gets an empty 304. The body is built from memory either way;
/// the tag only saves the transfer and the client's parsing.
/// </summary>
internal static class CacheableJson
{
    public static async Task WriteAsync(HttpContext http, JsonObject body, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body.ToJsonString());
        var tag = ETagOf(bytes);
        http.Response.Headers.ETag = tag;
        // Per key: the answer depends on who asks, so shared caches must not keep it.
        http.Response.Headers.CacheControl = "private, no-cache";
        if (Matches(http.Request.Headers.IfNoneMatch, tag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        http.Response.ContentType = "application/json";
        http.Response.ContentLength = bytes.Length;
        await http.Response.Body.WriteAsync(bytes, ct);
    }

    internal static string ETagOf(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, hash);
        return "\"" + Convert.ToHexStringLower(hash[..16]) + "\"";
    }

    private static bool Matches(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string tag)
    {
        if (ifNoneMatch.Count == 0)
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseList(ifNoneMatch, out var tags))
        {
            return false;
        }

        foreach (var candidate in tags)
        {
            // Weak comparison (RFC 9110 §13.1.2): a W/ prefix added by a proxy still matches.
            if (candidate.Equals(EntityTagHeaderValue.Any) || candidate.Tag.Equals(tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
