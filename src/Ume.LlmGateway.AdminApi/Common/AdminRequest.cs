using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Ume.LlmGateway.AdminApi.Common;

public abstract record AdminRequest;

public sealed class AdminValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var argument in context.Arguments.OfType<AdminRequest>())
        {
            Validate(argument, "", errors);
        }
        return errors.Count > 0 ? Results.ValidationProblem(errors, title: ApiFaultException.ValidationTitle) : await next(context);
    }

    private static void Validate(AdminRequest argument, string prefix, Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(argument, new ValidationContext(argument), results, validateAllProperties: true);
        foreach (var result in results)
        {
            foreach (var member in result.MemberNames.DefaultIfEmpty("request"))
            {
                var field = prefix + JsonNamingPolicy.CamelCase.ConvertName(member);
                errors[field] = [result.ErrorMessage ?? "The value is invalid."];
            }
        }
        foreach (var property in argument.GetType().GetProperties())
        {
            if (property.GetValue(argument) is AdminRequest nested)
            {
                Validate(nested, prefix + property.Name + ".", errors);
            }
            else if (property.GetValue(argument) is IEnumerable<AdminRequest> children)
            {
                var i = 0;
                foreach (var child in children)
                {
                    Validate(child, $"{prefix}{property.Name}[{i++}].", errors);
                }
            }
        }
    }
}
