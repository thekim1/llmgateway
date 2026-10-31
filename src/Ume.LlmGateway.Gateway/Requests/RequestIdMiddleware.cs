namespace Ume.LlmGateway.Gateway.Requests;

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
