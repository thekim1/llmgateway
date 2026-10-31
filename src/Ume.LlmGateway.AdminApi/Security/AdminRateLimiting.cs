using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Ume.LlmGateway.AdminApi.Common;

namespace Ume.LlmGateway.AdminApi.Security;

public static class AdminRateLimiting
{
    /// <summary>
    /// <see cref="AdminPolicies.PerUserRateLimit"/>: <see cref="AdminOptions.RequestsPerMinute"/> per signed-in user, or per
    /// client address before sign-in, in fixed one-minute windows without a queue.
    /// </summary>
    public static IServiceCollection AddAdminRateLimiting(this IServiceCollection services) => services.AddRateLimiter(o =>
    {
        o.AddPolicy(AdminPolicies.PerUserRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirst(AdminClaims.Subject)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = http.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        o.RejectWithProblem("Too many requests. Wait a minute.");
    });
}
