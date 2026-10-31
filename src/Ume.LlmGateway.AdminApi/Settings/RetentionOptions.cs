using System.ComponentModel.DataAnnotations;

namespace Ume.LlmGateway.AdminApi.Settings;

/// <summary>Bound from the <c>Retention</c> configuration section. How long data is kept is set in the admin UI (Settings).</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Run the nightly removal. Off, nothing is removed except by <c>Run now</c> in the admin UI.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Local time (Europe/Stockholm) of the nightly run.</summary>
    [RegularExpression("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    public string RunAt { get; set; } = "03:00";

    /// <summary>Rows removed per database call; each call is its own short transaction.</summary>
    [Range(100, 50_000)]
    public int BatchSize { get; set; } = 5000;
}
