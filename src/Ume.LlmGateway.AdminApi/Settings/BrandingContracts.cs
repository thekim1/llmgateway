using System.ComponentModel.DataAnnotations;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Entities;

namespace Ume.LlmGateway.AdminApi.Settings;

/// <summary>The organisation name shown under "AI Gateway" in the menu and on the sign-in page; empty shows none.</summary>
public sealed record BrandingRequest(
    [property: Required(AllowEmptyStrings = true), MaxLength(Branding.MaxOrganisationNameLength)] string OrganisationName) : AdminRequest;

/// <summary>
/// A new logo, base64-encoded: PNG, JPEG, WebP or SVG, at most <see cref="Branding.MaxLogoBytes"/> bytes. The type is
/// detected from the content, not taken from the client.
/// </summary>
public sealed record LogoRequest([property: Required] string Data) : AdminRequest;

/// <summary>
/// What the admin console shows as its owner. <c>LogoUrl</c>: where to load the logo from (changes with the logo, so it
/// can be cached); null = the built-in icon.
/// </summary>
public sealed record BrandingDto(string OrganisationName, string? LogoUrl, DateTimeOffset? UpdatedAt);
