using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ume.LlmGateway.AdminApi.Auditing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.Domain.Entities;
using Ume.LlmGateway.Infrastructure.Persistence;

namespace Ume.LlmGateway.AdminApi.Settings;

/// <summary>Changes the admin console's branding (the single <see cref="Branding"/> row). Gateway admins only.</summary>
public sealed class BrandingService(AdminContext ctx, BrandingCache cache)
{
    public async Task<BrandingDto> SetNameAsync(BrandingRequest input, CancellationToken ct) =>
        await ChangeAsync(b => b.OrganisationName = input.OrganisationName.Trim(), ct);

    public async Task<BrandingDto> SetLogoAsync(LogoRequest input, CancellationToken ct)
    {
        var (logo, contentType) = LogoImage.Parse(input.Data);
        return await ChangeAsync(b =>
        {
            b.Logo = logo;
            b.LogoContentType = contentType;
        }, ct);
    }

    public async Task<BrandingDto> RemoveLogoAsync(CancellationToken ct) => await ChangeAsync(b =>
    {
        b.Logo = null;
        b.LogoContentType = null;
    }, ct);

    private async Task<BrandingDto> ChangeAsync(Action<Branding> change, CancellationToken ct)
    {
        var branding = await ctx.Db.Branding.SingleOrDefaultAsync(ct);
        if (branding is null)
        {
            // Production creates the row in app-roles.sql (the admin role may only update it); development databases may lack it.
            branding = new Branding();
            ctx.Db.Branding.Add(branding);
        }

        var before = BrandingAudit.Of(branding);
        change(branding);
        branding.UpdatedAt = ctx.Now;
        try
        {
            await ctx.SaveAsync(AuditActions.Update, AuditEntities.Settings, Branding.SingletonId, before, BrandingAudit.Of(branding), [], ct);
        }
        catch (DbUpdateException)
        {
            throw new ApiFaultException(409, "Someone else changed the branding at the same time. Reload the page and try again.");
        }

        cache.Invalidate();
        return BrandingCache.Snapshot.Of(branding).Dto;
    }
}

/// <summary>
/// The branding as the anonymous <c>/bff/branding</c> endpoints serve it (the sign-in page shows it too). Held for
/// <see cref="Lifetime"/> so page loads do not query the database; another admin API instance shows a change after that.
/// </summary>
public sealed class BrandingCache(IServiceScopeFactory scopes, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _loading = new(1, 1);
    private Snapshot? _current;
    private DateTimeOffset _expires;

    public async Task<Snapshot> GetAsync(CancellationToken ct)
    {
        if (_current is { } current && time.GetUtcNow() < _expires)
        {
            return current;
        }

        await _loading.WaitAsync(ct);
        try
        {
            if (_current is { } loaded && time.GetUtcNow() < _expires)
            {
                return loaded;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
            var row = await db.Branding.AsNoTracking().SingleOrDefaultAsync(ct) ?? new Branding();
            _current = Snapshot.Of(row);
            _expires = time.GetUtcNow() + Lifetime;
            return _current;
        }
        finally
        {
            _loading.Release();
        }
    }

    public void Invalidate() => _expires = DateTimeOffset.MinValue;

    public void Dispose() => _loading.Dispose();

    /// <summary>One loaded state. <c>Version</c>: a short hash of the logo, which makes its URL change with it.</summary>
    public sealed record Snapshot(BrandingDto Dto, byte[]? Logo, string? LogoContentType, string? Version)
    {
        public const string LogoPath = "/bff/branding/logo";

        public static Snapshot Of(Branding b)
        {
            var version = b.Logo is { } logo ? Convert.ToHexStringLower(SHA256.HashData(logo))[..16] : null;
            return new Snapshot(new BrandingDto(b.OrganisationName, version is null ? null : $"{LogoPath}?v={version}", b.UpdatedAt),
                b.Logo, b.LogoContentType, version);
        }
    }
}

/// <summary>Checks an uploaded logo and finds its type from its first bytes.</summary>
public static partial class LogoImage
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string WebP = "image/webp";
    public const string Svg = "image/svg+xml";

    public static (byte[] Data, string ContentType) Parse(string base64)
    {
        byte[] data;
        try
        {
            data = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new ApiFaultException(400, "The logo is not valid base64.");
        }

        if (data.Length == 0)
        {
            throw new ApiFaultException(400, "The logo is empty.");
        }

        if (data.Length > Branding.MaxLogoBytes)
        {
            throw new ApiFaultException(400, $"The logo is larger than {Branding.MaxLogoBytes / 1024} KB. Use a smaller image; it is shown at 36 pixels.");
        }

        return (data, Detect(data) ?? throw new ApiFaultException(400, "The logo must be a PNG, JPEG, WebP or SVG image."));
    }

    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return WebP;
        }

        return IsSvg(data) ? Svg : null;
    }

    /// <summary>
    /// An SVG document without scripts or event handlers. The console shows it in an <c>img</c> element, where scripts never
    /// run, and serves it with a sandboxing Content-Security-Policy; refusing them as well keeps a hostile file out entirely.
    /// </summary>
    private static bool IsSvg(ReadOnlySpan<byte> data)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var start = text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || text.AsSpan(0, start).ContainsAny('\0', '\u0001'))
        {
            return false;
        }

        if (text.Contains("<script", StringComparison.OrdinalIgnoreCase) || text.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase) || OnAttribute().IsMatch(text))
        {
            throw new ApiFaultException(400, "SVG logos may not contain scripts, event handlers or embedded HTML.");
        }

        return true;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\son[a-z]+\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OnAttribute();
}
