using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ume.LlmGateway.AdminApi;

/// <summary>The newest published release (drafts and pre-releases excluded).</summary>
public sealed record LatestReleaseDto(string Version, string Name, string Url, DateTimeOffset? PublishedAt);

/// <summary>
/// The running version and the newest release. <c>DevelopmentBuild</c>: built locally, not from a release tag, so it is never
/// reported as out of date. <c>CheckEnabled</c>: false when <c>Admin:CheckForReleases</c> is off. <c>Latest</c>: null before the
/// first successful check, when the check is off, or when the repository has no releases. <c>CheckFailed</c>: the last attempt
/// failed; <c>Latest</c> is then what an earlier check found, if anything.
/// </summary>
public sealed record VersionInfoDto(
    string Version, bool DevelopmentBuild, bool CheckEnabled, LatestReleaseDto? Latest, bool UpdateAvailable, DateTimeOffset? CheckedAt, bool CheckFailed);

/// <summary>
/// Asks GitHub for the latest release of <see cref="AdminOptions.ReleaseRepository"/>, at most every few hours (the
/// unauthenticated API allows 60 requests an hour per address). Only the admin API calls out; browsers never do.
/// </summary>
public sealed partial class ReleaseCheck(IHttpClientFactory clients, IOptions<AdminOptions> options, TimeProvider time, ILogger<ReleaseCheck> logger)
    : IDisposable
{
    public const string HttpClientName = "github-releases";
    private const string ApiHost = "api.github.com";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    /// <summary>The version the admin API was built as, without build metadata (<c>0.6.1</c>, or <c>0.0.0-local</c> locally).</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LatestReleaseDto? _latest;
    private DateTimeOffset? _checkedAt;
    private bool _failed;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public async Task<VersionInfoDto> GetAsync(CancellationToken ct)
    {
        var current = CurrentVersion;
        var development = IsDevelopmentBuild(current);
        if (!options.Value.CheckForReleases)
        {
            return new VersionInfoDto(current, development, false, null, false, null, false);
        }

        await RefreshAsync(ct);
        var latest = _latest;
        return new VersionInfoDto(current, development, true, latest,
            !development && latest is not null && CompareVersions(latest.Version, current) > 0, _checkedAt, _failed);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() < _expires)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (time.GetUtcNow() < _expires)
            {
                return;
            }
            try
            {
                _latest = await LoadAsync(ct);
                _failed = false;
                _expires = time.GetUtcNow() + Lifetime;
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException ||
                                      (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogUnavailable(logger, e.Message);
                _failed = true;
                _expires = time.GetUtcNow() + RetryAfterFailure;
            }
            _checkedAt = time.GetUtcNow();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LatestReleaseDto?> LoadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{ApiHost}/repos/{options.Value.ReleaseRepository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Ume-LlmGateway", CurrentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null; // no release published yet
        }
        response.EnsureSuccessStatusCode();

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token)) as JsonObject
            ?? throw new InvalidOperationException("The release is not a JSON object.");
        var tag = Text(body, "tag_name") ?? string.Empty;
        if (!ReleaseTag().IsMatch(tag))
        {
            throw new InvalidOperationException($"The latest release tag '{tag[..Math.Min(tag.Length, 40)]}' is not a version.");
        }
        var version = tag.TrimStart('v');
        // Only a link to the release on GitHub itself is shown to admins.
        var url = Text(body, "html_url") is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                  string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? uri.ToString()
            : $"https://github.com/{options.Value.ReleaseRepository}/releases/tag/{Uri.EscapeDataString(tag)}";
        var name = Text(body, "name") is { Length: > 0 } n ? n[..Math.Min(n.Length, 200)] : tag;
        var published = Text(body, "published_at") is { } p && DateTimeOffset.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : (DateTimeOffset?)null;
        return new LatestReleaseDto(version, name, url, published);
    }

    /// <summary>Local builds carry the placeholder <c>0.0.0-local</c> from Directory.Build.props.</summary>
    public static bool IsDevelopmentBuild(string version) => version.StartsWith("0.0.0", StringComparison.Ordinal);

    /// <summary>
    /// Orders <c>major.minor.patch[-prerelease]</c> versions: a release is newer than its own pre-releases, and pre-release
    /// labels of the same version compare as text. Unparseable versions compare as equal, so they never claim an update.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        if (!Version.TryParse(coreA, out var va) || !Version.TryParse(coreB, out var vb))
        {
            return 0;
        }
        var core = va.CompareTo(vb);
        if (core != 0)
        {
            return core;
        }
        return (preA, preB) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(preA, preB),
        };

        static (string Core, string? Prerelease) Split(string version)
        {
            var v = version.TrimStart('v');
            var plus = v.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                v = v[..plus];
            }
            var dash = v.IndexOf('-', StringComparison.Ordinal);
            return dash < 0 ? (v, null) : (v[..dash], v[(dash + 1)..]);
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string ReadCurrentVersion()
    {
        var assembly = typeof(ReleaseCheck).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    [GeneratedRegex(@"^v?\d{1,6}\.\d{1,6}\.\d{1,6}(-[0-9A-Za-z.-]{1,40})?$")]
    private static partial Regex ReleaseTag();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check GitHub for a newer release: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
