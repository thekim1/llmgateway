using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Hosting;

/// <summary>How every host binds its settings.</summary>
public static class OptionsDefaults
{
    /// <summary>
    /// Binds <typeparamref name="T"/> to <paramref name="section"/> and validates it on start (DataAnnotations and
    /// <see cref="IValidatableObject"/>), so a wrong setting stops the host instead of failing a request later.
    /// </summary>
    public static OptionsBuilder<T> AddValidatedOptions<T>(this IServiceCollection services, string section) where T : class =>
        services.AddOptions<T>().BindConfiguration(section).ValidateDataAnnotations().ValidateOnStart();
}
