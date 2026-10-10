namespace Ume.LlmGateway.ServiceDefaults;

/// <summary>
/// A request that cannot be carried out for a reason the caller should see (not found, conflict, invalid input).
/// <see cref="ApiExceptionHandler"/> turns it into a problem response with <see cref="Status"/> and the message as detail.
/// </summary>
public sealed class ApiFaultException : Exception
{
    /// <summary>Title of validation problems (field errors).</summary>
    public const string ValidationTitle = "Kontrollera de markerade fälten.";

    public ApiFaultException(int status, string detail, IDictionary<string, object?>? extensions = null) : base(detail)
    {
        Status = status;
        Extensions = extensions;
    }

    private ApiFaultException(IDictionary<string, string[]> errors) : base(ValidationTitle)
    {
        Status = 400;
        Errors = errors;
    }

    public int Status { get; }

    /// <summary>Extra machine-readable members for the problem response (e.g. the choices a client can offer the user).</summary>
    public IDictionary<string, object?>? Extensions { get; }

    /// <summary>Field errors (field name to messages); set for validation faults, which are answered as a validation problem.</summary>
    public IDictionary<string, string[]>? Errors { get; }

    /// <summary>A 400 validation problem with <see cref="ValidationTitle"/> and the given field errors.</summary>
    public static ApiFaultException Validation(IDictionary<string, string[]> errors) => new(errors);
}
