namespace Ume.LlmGateway.Domain.Entities;

/// <summary>
/// What the admin console shows as its owner: the organisation name under "AI Gateway" and the logo in the menu and on the
/// sign-in page. One row (<see cref="SingletonId"/>), edited by gateway admins on the Settings page. Kept apart from
/// <see cref="GatewaySettings"/> so the gateway, which reads the settings with its catalogue, never loads the logo.
/// </summary>
public sealed class Branding
{
    public const int SingletonId = 1;

    /// <summary>The organisation name until a gateway admin sets another.</summary>
    public const string DefaultOrganisationName = "Umeå kommun";

    public const int MaxOrganisationNameLength = 80;

    /// <summary>Largest logo accepted, in bytes. The logo is shown at 36 px, so a small PNG or SVG is plenty.</summary>
    public const int MaxLogoBytes = 256 * 1024;

    public int Id { get; set; } = SingletonId;

    /// <summary>Shown under the product name; empty shows no name.</summary>
    public string OrganisationName { get; set; } = DefaultOrganisationName;

    /// <summary>The logo image; null shows the built-in icon.</summary>
    public byte[]? Logo { get; set; }

    /// <summary>The logo's media type, detected from its content (<c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>, <c>image/svg+xml</c>).</summary>
    public string? LogoContentType { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
