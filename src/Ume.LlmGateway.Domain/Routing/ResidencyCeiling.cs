namespace Ume.LlmGateway.Domain.Routing;

/// <summary>
/// The highest data residency a single request allows (request header <c>x-ume-max-residency</c>). The order of
/// <see cref="DataResidency"/> is the order of the ceiling: <c>OnPrem</c> &lt; <c>Eu</c> &lt; <c>External</c>. A ceiling
/// only narrows what the key allows; it can never widen it.
/// </summary>
public static class ResidencyCeiling
{
    /// <summary>
    /// Parses one of the <see cref="DataResidency"/> names, case-insensitively, surrounding spaces ignored. Numbers,
    /// aliases (<c>Any</c>) and lists are refused: a ceiling that is not understood must fail, not be ignored.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> value, out DataResidency ceiling)
    {
        value = value.Trim();
        if (value.Equals(nameof(DataResidency.OnPrem), StringComparison.OrdinalIgnoreCase))
        {
            ceiling = DataResidency.OnPrem;
            return true;
        }

        if (value.Equals(nameof(DataResidency.Eu), StringComparison.OrdinalIgnoreCase))
        {
            ceiling = DataResidency.Eu;
            return true;
        }

        if (value.Equals(nameof(DataResidency.External), StringComparison.OrdinalIgnoreCase))
        {
            ceiling = DataResidency.External;
            return true;
        }

        ceiling = default;
        return false;
    }
}
