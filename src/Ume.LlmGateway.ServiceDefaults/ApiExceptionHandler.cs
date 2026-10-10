using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ume.LlmGateway.ServiceDefaults;

/// <summary>How <see cref="ApiExceptionHandler"/> answers failed requests.</summary>
public sealed class ApiExceptionOptions
{
    /// <summary>Detail of every 500 response; the exception itself is never shown.</summary>
    public const string InternalErrorDetail = "Ett internt fel inträffade.";

    /// <summary>Title of every problem response except validation problems; <c>null</c> uses the status code's reason phrase.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Map host-specific exceptions (e.g. a database unique-key violation) to a fault the caller should see.
    /// The first translator that returns a fault wins; unmatched exceptions become a logged 500.
    /// </summary>
    public IList<Func<Exception, ApiFaultException?>> Translators { get; } = [];
}

/// <summary>
/// Answers <see cref="ApiFaultException"/> (and translated exceptions) with its status and message as a problem response,
/// bad requests with 400, and everything else with a 500 that only logs the exception type.
/// </summary>
internal sealed partial class ApiExceptionHandler(IOptions<ApiExceptionOptions> options, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var fault = exception as ApiFaultException ?? settings.Translators.Select(translate => translate(exception)).FirstOrDefault(f => f is not null);
        IResult result;
        if (fault?.Errors is { } errors)
        {
            result = Results.ValidationProblem(errors, title: ApiFaultException.ValidationTitle);
        }
        else if (fault is not null)
        {
            result = Results.Problem(statusCode: fault.Status, title: settings.Title, detail: fault.Message, extensions: fault.Extensions);
        }
        else if (exception is BadHttpRequestException bad)
        {
            result = Results.Problem(statusCode: bad.StatusCode, title: settings.Title, detail: bad.Message);
        }
        else
        {
            LogFailed(logger, exception.GetType().Name);
            result = Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: settings.Title, detail: ApiExceptionOptions.InternalErrorDetail);
        }

        await result.ExecuteAsync(httpContext);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Request failed ({ExceptionType})")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
