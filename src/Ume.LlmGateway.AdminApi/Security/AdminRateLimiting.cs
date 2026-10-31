using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;

namespace Ume.LlmGateway.AdminApi.Security;

public static class AdminRateLimiting
{
    /// <summary>
    /// <see cref="AdminPolicies.PerUserRateLimit"/>: <see cref="AdminOptions.RequestsPerMinute"/> per signed-in user, or per
    /// client address before sign-in, in fixed one-minute windows without a queue.
    /// </summary>
    public static IServiceCollection AddAdminRateLimiting(this IServiceCollection services) => services.AddRateLimiter(o => o
        .AddPerMinutePolicy(AdminPolicies.PerUserRateLimit,
            http => http.User.FindFirst(AdminClaims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            http => http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.RequestsPerMinute)
        .RejectWithProblem("Too many requests. Wait a minute."));
}
