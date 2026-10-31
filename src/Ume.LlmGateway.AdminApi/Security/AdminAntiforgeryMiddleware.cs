using Microsoft.AspNetCore.Antiforgery;

namespace Ume.LlmGateway.AdminApi.Security;

public sealed class AdminAntiforgeryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if ((http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || http.Request.Path.StartsWithSegments("/bff", StringComparison.OrdinalIgnoreCase)) &&
            !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
        {
            if (http.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
            {
                await Results.Problem(statusCode: 400, detail: "CSRF protection is missing. Reload the page.").ExecuteAsync(http);
                return;
            }
            try
            {
                await antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem(statusCode: 400, detail: "The CSRF token is invalid. Reload the page.").ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }
}
