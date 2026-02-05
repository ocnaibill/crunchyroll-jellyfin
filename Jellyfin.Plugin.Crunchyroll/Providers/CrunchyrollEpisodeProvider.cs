using Jellyfin.Plugin.Crunchyroll.Api;
using Jellyfin.Plugin.Crunchyroll.Models;
using Jellyfin.Plugin.Crunchyroll.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Providers;

/// <summary>
/// Metadata provider for anime episodes from Crunchyroll.
/// Handles the complex mapping between Jellyfin episode numbering and Crunchyroll's continuous numbering.
/// </summary>
public class CrunchyrollEpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>, IHasOrder
{
    private readonly ILogger<CrunchyrollEpisodeProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollEpisodeProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CrunchyrollEpisodeProvider(ILogger<CrunchyrollEpisodeProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Crunchyroll";

    /// <inheritdoc />
    public int Order => 3;

    /// <inheritdoc />
    public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Episode>();

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";

        using var httpClient = _httpClientFactory.CreateClient();
        var flareSolverrUrl = config?.FlareSolverrUrl;
        var username = config?.Username;
        var password = config?.Password;
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale, flareSolverrUrl, username, password);

        // Check for existing Crunchyroll episode ID
        string? crunchyrollEpisodeId = info.GetProviderId("CrunchyrollEpisode");
        if (!string.IsNullOrEmpty(crunchyrollEpisodeId))
        {
            var episode = await apiClient.GetEpisodeAsync(crunchyrollEpisodeId, cancellationToken).ConfigureAwait(false);
            if (episode != null)
            {
                return CreateEpisodeResult(episode);
            }
        }

        // Get series ID from parent
        string? seriesId = info.SeriesProviderIds?.GetValueOrDefault("Crunchyroll");
        if (string.IsNullOrEmpty(seriesId))
        {
            _logger.LogDebug("No Crunchyroll series ID found for episode: {Name}", info.Name);
            return result;
        }

        // Get all seasons and episodes
        var seasons = await apiClient.GetSeasonsAsync(seriesId, cancellationToken).ConfigureAwait(false);
        if (seasons.Count == 0)
        {
            _logger.LogWarning("No seasons found for series: {SeriesId}", seriesId);
            return result;
        }

        // Fetch episodes for all seasons
        var allEpisodes = new Dictionary<string, List<CrunchyrollEpisode>>();
        foreach (var season in seasons)
        {
            if (string.IsNullOrEmpty(season.Id))
            {
                continue;
            }

            var episodes = await apiClient.GetEpisodesAsync(season.Id, cancellationToken).ConfigureAwait(false);
            allEpisodes[season.Id] = episodes;
        }

        // Use the episode mapping service
        var mappingService = new EpisodeMappingService(_logger);
        var seasonMapping = mappingService.CalculateSeasonMapping(seriesId, seasons, allEpisodes);

        int jellyfinSeason = info.ParentIndexNumber ?? 1;
        int jellyfinEpisode = info.IndexNumber ?? 0;

        _logger.LogDebug(
            "Looking for Jellyfin S{Season}E{Episode} in series {SeriesId}",
            jellyfinSeason,
            jellyfinEpisode,
            seriesId);

        if (config?.EnableEpisodeOffsetMapping == true)
        {
            // Use smart episode matching with offset calculation
            var matchResult = mappingService.FindMatchingEpisode(
                jellyfinSeason,
                jellyfinEpisode,
                seasonMapping,
                allEpisodes);

            if (matchResult.Success && matchResult.Episode != null)
            {
                _logger.LogInformation(
                    "Matched Jellyfin S{JellyfinS}E{JellyfinE} to Crunchyroll episode '{Title}' (Confidence: {Confidence}%)",
                    jellyfinSeason,
                    jellyfinEpisode,
                    matchResult.Episode.Title,
                    matchResult.Confidence);

                return CreateEpisodeResult(matchResult.Episode);
            }
        }
        else
        {
            // Fallback: Simple matching by season position and episode number
            var matchedEpisode = FindEpisodeSimple(jellyfinSeason, jellyfinEpisode, seasons, allEpisodes);
            if (matchedEpisode != null)
            {
                return CreateEpisodeResult(matchedEpisode);
            }
        }

        _logger.LogWarning(
            "Could not find matching episode for S{Season}E{Episode}",
            jellyfinSeason,
            jellyfinEpisode);

        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();

        // Episodes are typically not searched directly, they're matched through series
        // But we support it for manual overrides

        string? crunchyrollEpisodeId = searchInfo.GetProviderId("CrunchyrollEpisode");
        if (!string.IsNullOrEmpty(crunchyrollEpisodeId))
        {
            var config = Plugin.Instance?.Configuration;
            var locale = config?.PreferredLanguage ?? "pt-BR";

            using var httpClient = _httpClientFactory.CreateClient();
            var flareSolverrUrl = config?.FlareSolverrUrl;
            var username = config?.Username;
            var password = config?.Password;
            using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale, flareSolverrUrl, username, password);

            var episode = await apiClient.GetEpisodeAsync(crunchyrollEpisodeId, cancellationToken).ConfigureAwait(false);
            if (episode != null)
            {
                results.Add(CreateSearchResult(episode));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
    }

    private MetadataResult<Episode> CreateEpisodeResult(CrunchyrollEpisode crEpisode)
    {
        var result = new MetadataResult<Episode>
        {
            HasMetadata = true,
            Item = new Episode
            {
                Name = crEpisode.Title,
                Overview = crEpisode.Description,
                IndexNumber = crEpisode.EpisodeNumberInt,
                ParentIndexNumber = crEpisode.SeasonNumber
            }
        };

        // Set runtime
        if (crEpisode.DurationMs > 0)
        {
            result.Item.RunTimeTicks = TimeSpan.FromMilliseconds(crEpisode.DurationMs).Ticks;
        }

        // Set premiere date
        if (!string.IsNullOrEmpty(crEpisode.EpisodeAirDate) && 
            DateTime.TryParse(crEpisode.EpisodeAirDate, out var airDate))
        {
            result.Item.PremiereDate = airDate;
        }

        // Set official rating
        if (crEpisode.MaturityRatings != null && crEpisode.MaturityRatings.Count > 0)
        {
            result.Item.OfficialRating = crEpisode.MaturityRatings[0];
        }

        // Set provider IDs
        if (!string.IsNullOrEmpty(crEpisode.Id))
        {
            result.Item.SetProviderId("CrunchyrollEpisode", crEpisode.Id);
        }

        return result;
    }

    private RemoteSearchResult CreateSearchResult(CrunchyrollEpisode episode)
    {
        var result = new RemoteSearchResult
        {
            Name = $"{episode.SeriesTitle} - S{episode.SeasonNumber}E{episode.EpisodeNumber} - {episode.Title}",
            Overview = episode.Description,
            SearchProviderName = "Crunchyroll"
        };

        if (!string.IsNullOrEmpty(episode.Id))
        {
            result.SetProviderId("CrunchyrollEpisode", episode.Id);
        }

        // Get thumbnail
        var thumbnail = episode.Images?.Thumbnail?.FirstOrDefault()?.FirstOrDefault();
        if (thumbnail?.Source != null)
        {
            result.ImageUrl = thumbnail.Source;
        }

        return result;
    }

    /// <summary>
    /// Simple episode matching without offset calculation.
    /// Matches by finding the Nth season and Mth episode.
    /// </summary>
    private CrunchyrollEpisode? FindEpisodeSimple(
        int jellyfinSeason,
        int jellyfinEpisode,
        List<CrunchyrollSeason> seasons,
        Dictionary<string, List<CrunchyrollEpisode>> allEpisodes)
    {
        // Sort seasons and get the one at the requested position
        var sortedSeasons = seasons
            .Where(s => s.AudioLocales?.Contains("ja-JP") == true) // Prefer Japanese
            .OrderBy(s => s.SeasonSequenceNumber)
            .ThenBy(s => s.SeasonNumber)
            .ToList();

        // If no Japanese versions, use all
        if (sortedSeasons.Count == 0)
        {
            sortedSeasons = seasons
                .OrderBy(s => s.SeasonSequenceNumber)
                .ThenBy(s => s.SeasonNumber)
                .ToList();
        }

        if (jellyfinSeason <= 0 || jellyfinSeason > sortedSeasons.Count)
        {
            return null;
        }

        var targetSeason = sortedSeasons[jellyfinSeason - 1];
        if (string.IsNullOrEmpty(targetSeason.Id))
        {
            return null;
        }

        var episodes = allEpisodes.GetValueOrDefault(targetSeason.Id, new List<CrunchyrollEpisode>());
        
        // Sort episodes and find by position
        var sortedEpisodes = episodes
            .Where(e => !e.IsClip)
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        if (jellyfinEpisode <= 0 || jellyfinEpisode > sortedEpisodes.Count)
        {
            return null;
        }

        return sortedEpisodes[jellyfinEpisode - 1];
    }
}
