using Jellyfin.Plugin.Crunchyroll.Api;
using Jellyfin.Plugin.Crunchyroll.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Providers;

/// <summary>
/// Metadata provider for anime seasons from Crunchyroll.
/// </summary>
public class CrunchyrollSeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    private readonly ILogger<CrunchyrollSeasonProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollSeasonProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CrunchyrollSeasonProvider(ILogger<CrunchyrollSeasonProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Crunchyroll";

    /// <inheritdoc />
    public int Order => 3;

    /// <inheritdoc />
    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Season>();

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";
        var fallbackLocale = config?.FallbackLanguage ?? "en-US";

        using var httpClient = _httpClientFactory.CreateClient();
        var flareSolverrUrl = config?.FlareSolverrUrl;
        var username = config?.Username;
        var password = config?.Password;
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale, flareSolverrUrl, username, password);

        // Check for existing Crunchyroll season ID
        string? crunchyrollSeasonId = info.GetProviderId("CrunchyrollSeason");
        if (!string.IsNullOrEmpty(crunchyrollSeasonId))
        {
            // We can't get a single season directly, so we need to get via series
            string? seriesId = info.SeriesProviderIds?.GetValueOrDefault("Crunchyroll");
            if (!string.IsNullOrEmpty(seriesId))
            {
                var seasons = await apiClient.GetSeasonsAsync(seriesId, cancellationToken).ConfigureAwait(false);
                var season = seasons.FirstOrDefault(s => s.Id == crunchyrollSeasonId);
                if (season != null)
                {
                    return CreateSeasonResult(season, info.IndexNumber ?? 1);
                }
            }
        }

        // Get series ID from parent
        string? parentSeriesId = info.SeriesProviderIds?.GetValueOrDefault("Crunchyroll");
        if (string.IsNullOrEmpty(parentSeriesId))
        {
            _logger.LogDebug("No Crunchyroll series ID found for season");
            return result;
        }

        var allSeasons = await apiClient.GetSeasonsAsync(parentSeriesId, cancellationToken).ConfigureAwait(false);
        
        // Match season by SeasonNumber (not by position!)
        // This fixes Issue #2: geo-blocked seasons should not shift the mapping
        int jellyfinSeasonNumber = info.IndexNumber ?? 1;
        var matchedSeason = FindMatchingSeasonByNumber(jellyfinSeasonNumber, allSeasons);

        if (matchedSeason != null)
        {
            return CreateSeasonResult(matchedSeason, jellyfinSeasonNumber);
        }

        // Solution B: Try fallback locale if season not found
        // This may help with geo-blocked content that's available in other regions
        if (locale != fallbackLocale && allSeasons.Count > 0)
        {
            _logger.LogInformation(
                "Season {SeasonNumber} not found in {Locale}, trying fallback locale {FallbackLocale}",
                jellyfinSeasonNumber, locale, fallbackLocale);

            using var fallbackHttpClient = _httpClientFactory.CreateClient();
            using var fallbackApiClient = new CrunchyrollApiClient(
                fallbackHttpClient, _logger, fallbackLocale, flareSolverrUrl, username, password);

            var fallbackSeasons = await fallbackApiClient.GetSeasonsAsync(parentSeriesId, cancellationToken)
                .ConfigureAwait(false);

            matchedSeason = FindMatchingSeasonByNumber(jellyfinSeasonNumber, fallbackSeasons);

            if (matchedSeason != null)
            {
                _logger.LogInformation(
                    "Found Season {SeasonNumber} using fallback locale {FallbackLocale}",
                    jellyfinSeasonNumber, fallbackLocale);
                return CreateSeasonResult(matchedSeason, jellyfinSeasonNumber);
            }
        }

        _logger.LogWarning(
            "Season {SeasonNumber} not found in Crunchyroll (tried locales: {Locale}, {FallbackLocale})",
            jellyfinSeasonNumber, locale, fallbackLocale);

        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();

        string? seriesId = searchInfo.SeriesProviderIds?.GetValueOrDefault("Crunchyroll");
        if (string.IsNullOrEmpty(seriesId))
        {
            return results;
        }

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";

        using var httpClient = _httpClientFactory.CreateClient();
        var flareSolverrUrl = config?.FlareSolverrUrl;
        var username = config?.Username;
        var password = config?.Password;
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale, flareSolverrUrl, username, password);

        var seasons = await apiClient.GetSeasonsAsync(seriesId, cancellationToken).ConfigureAwait(false);

        foreach (var season in seasons)
        {
            results.Add(CreateSearchResult(season));
        }

        return results;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
    }

    private MetadataResult<Season> CreateSeasonResult(CrunchyrollSeason crSeason, int displaySeasonNumber)
    {
        var result = new MetadataResult<Season>
        {
            HasMetadata = true,
            Item = new Season
            {
                Name = crSeason.Title,
                Overview = crSeason.Description,
                IndexNumber = displaySeasonNumber
            }
        };

        // Set provider ID
        if (!string.IsNullOrEmpty(crSeason.Id))
        {
            result.Item.SetProviderId("CrunchyrollSeason", crSeason.Id);
        }

        _logger.LogInformation(
            "Matched Jellyfin Season {DisplayNumber} to Crunchyroll season: {Title}",
            displaySeasonNumber,
            crSeason.Title);

        return result;
    }

    private RemoteSearchResult CreateSearchResult(CrunchyrollSeason season)
    {
        var result = new RemoteSearchResult
        {
            Name = $"Season {season.SeasonNumber}: {season.Title}",
            Overview = season.Description,
            SearchProviderName = "Crunchyroll"
        };

        if (!string.IsNullOrEmpty(season.Id))
        {
            result.SetProviderId("CrunchyrollSeason", season.Id);
        }

        return result;
    }

    /// <summary>
    /// Finds the matching Crunchyroll season by SeasonNumber.
    /// This matches Jellyfin S2 to Crunchyroll S2 directly, ignoring missing seasons.
    /// </summary>
    private CrunchyrollSeason? FindMatchingSeasonByNumber(int jellyfinSeasonNumber, List<CrunchyrollSeason> seasons)
    {
        // Filter to prefer Japanese audio versions (original)
        var preferredSeasons = seasons
            .Where(s => s.AudioLocales?.Contains("ja-JP") == true)
            .ToList();

        // If no Japanese versions found, use all seasons
        if (preferredSeasons.Count == 0)
        {
            preferredSeasons = seasons;
        }

        // Match by SeasonNumber directly (not by position!)
        // This fixes Issue #2: Jellyfin S2 -> Crunchyroll S2, even if S1 is missing
        var matchedSeason = preferredSeasons.FirstOrDefault(s => s.SeasonNumber == jellyfinSeasonNumber);

        if (matchedSeason != null)
        {
            return matchedSeason;
        }

        // For Season 0 (Specials), check for OADs, OVAs, Specials, Movies
        if (jellyfinSeasonNumber == 0)
        {
            matchedSeason = preferredSeasons.FirstOrDefault(s =>
                s.SeasonNumber == 0 ||
                s.Title?.Contains("OAD", StringComparison.OrdinalIgnoreCase) == true ||
                s.Title?.Contains("OVA", StringComparison.OrdinalIgnoreCase) == true ||
                s.Title?.Contains("Special", StringComparison.OrdinalIgnoreCase) == true ||
                s.Title?.Contains("Movie", StringComparison.OrdinalIgnoreCase) == true);

            return matchedSeason;
        }

        return null;
    }

    /// <summary>
    /// Finds the matching Crunchyroll season for a Jellyfin season number (legacy, by position).
    /// Kept for compatibility.
    /// </summary>
    private CrunchyrollSeason? FindMatchingSeason(int jellyfinSeasonNumber, List<CrunchyrollSeason> seasons)
    {
        // Filter to prefer Japanese audio versions (original)
        var preferredSeasons = seasons
            .Where(s => s.AudioLocales?.Contains("ja-JP") == true)
            .OrderBy(s => s.SeasonSequenceNumber)
            .ThenBy(s => s.SeasonNumber)
            .ToList();

        // If no Japanese versions found, use all seasons
        if (preferredSeasons.Count == 0)
        {
            preferredSeasons = seasons
                .OrderBy(s => s.SeasonSequenceNumber)
                .ThenBy(s => s.SeasonNumber)
                .ToList();
        }

        // Match by position (Jellyfin season 1 = first season in our list)
        if (jellyfinSeasonNumber > 0 && jellyfinSeasonNumber <= preferredSeasons.Count)
        {
            return preferredSeasons[jellyfinSeasonNumber - 1];
        }

        // If requested season is beyond available, return the last one
        if (jellyfinSeasonNumber > preferredSeasons.Count && preferredSeasons.Count > 0)
        {
            _logger.LogWarning(
                "Jellyfin season {JellyfinSeason} exceeds available Crunchyroll seasons ({Count}). Using last season.",
                jellyfinSeasonNumber,
                preferredSeasons.Count);
            return preferredSeasons.Last();
        }

        return null;
    }
}
