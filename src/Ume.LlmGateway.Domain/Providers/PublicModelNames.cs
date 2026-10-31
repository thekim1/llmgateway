using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.Domain.Providers;

/// <summary>
/// How clients name a model. A model's name is unique per provider, so two providers can offer <c>DeepSeek-V3</c>. Its
/// <em>public name</em> is the plain name while only one provider has it (names compare case-insensitively), and
/// <c>&lt;provider&gt;/&lt;name&gt;</c> once several do; <c>&lt;provider&gt;/&lt;name&gt;</c> always works. A plain name
/// several providers share is ambiguous and is refused: the gateway never picks a provider by itself. To spread or fall
/// back between providers an admin creates a route, which may carry the plain name.
/// </summary>
public static class PublicModelNames
{
    public static string Qualified(string providerName, string modelName) => $"{providerName}/{modelName}";

    /// <summary>The public name of each deployment, by id. Each deployment's <see cref="ModelDeployment.ProviderAccount"/> must be loaded.</summary>
    public static Dictionary<Guid, string> Public(IEnumerable<ModelDeployment> deployments)
    {
        ArgumentNullException.ThrowIfNull(deployments);
        var all = deployments.ToList();
        var shared = SharedNames(all.Select(d => d.Name));
        return all.ToDictionary(d => d.Id, d => shared.Contains(d.Name) ? Qualified(d.ProviderAccount!.Name, d.Name) : d.Name);
    }

    /// <summary>Plain names that more than one deployment has (case-insensitively).</summary>
    public static HashSet<string> SharedNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!seen.Add(name))
            {
                shared.Add(name);
            }
        }

        return shared;
    }
}
