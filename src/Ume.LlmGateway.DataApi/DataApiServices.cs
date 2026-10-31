using Microsoft.Extensions.Options;
using Ume.LlmGateway.DataApi.Authentication;

namespace Ume.LlmGateway.DataApi;

public static class DataApiServices
{
    /// <summary>The rate limit policy of every <c>/v1</c> endpoint.</summary>
    public const string PerClientRateLimit = "client";

    /// <summary>
    /// The Data API's options, authentication (OAuth client credentials or a Data API key) and
    /// <see cref="PerClientRateLimit"/>: <see cref="DataApiOptions.RequestsPerMinute"/> per client.
    /// </summary>
    public static IHostApplicationBuilder AddDataApiServices(this IHostApplicationBuilder builder)
    {
        builder.Services.AddValidatedOptions<DataApiOptions>(DataApiOptions.SectionName)
            .Validate(o => TimeZoneInfo.TryFindSystemTimeZoneById(o.TimeZone, out _), "DataApi:TimeZone is not a known time zone id.");
        builder.AddDataApiAuthentication();
        builder.Services.AddRateLimiter(o => o
            .AddPerMinutePolicy(PerClientRateLimit, http => DataApiAuthentication.ClientId(http.User),
                http => http.RequestServices.GetRequiredService<IOptions<DataApiOptions>>().Value.RequestsPerMinute)
            .RejectWithProblem("Too many requests for this client. Wait a minute."));
        return builder;
    }
}
