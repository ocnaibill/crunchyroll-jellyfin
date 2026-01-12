using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Crunchyroll.Configuration;

/// <summary>
/// Plugin configuration for the Crunchyroll metadata provider.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        PreferredLanguage = "pt-BR";
        FallbackLanguage = "en-US";
        EnableSeasonMapping = true;
        EnableEpisodeOffsetMapping = true;
        CacheExpirationHours = 24;
        FlareSolverrUrl = string.Empty;
    }

    /// <summary>
    /// Gets or sets the preferred language for metadata.
    /// </summary>
    public string PreferredLanguage { get; set; }

    /// <summary>
    /// Gets or sets the fallback language when preferred is not available.
    /// </summary>
    public string FallbackLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable automatic season mapping.
    /// When enabled, the plugin will try to match Jellyfin seasons with Crunchyroll seasons.
    /// </summary>
    public bool EnableSeasonMapping { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable episode offset mapping.
    /// When enabled, the plugin will handle cases where Crunchyroll episode numbers
    /// don't start at 1 for each season (e.g., Season 2 Episode 1 = Episode 25 on Crunchyroll).
    /// </summary>
    public bool EnableEpisodeOffsetMapping { get; set; }

    /// <summary>
    /// Gets or sets the cache expiration time in hours.
    /// </summary>
    public int CacheExpirationHours { get; set; }

    /// <summary>
    /// Gets or sets the FlareSolverr URL for bypassing Cloudflare protection.
    /// Example: http://localhost:8191
    /// Leave empty to try direct API access (may not work from server IPs).
    /// </summary>
    public string FlareSolverrUrl { get; set; }
}

