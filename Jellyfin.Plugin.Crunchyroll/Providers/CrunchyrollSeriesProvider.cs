using Jellyfin.Plugin.Crunchyroll.Api;
using Jellyfin.Plugin.Crunchyroll.Models;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Providers;

/// <summary>
/// Metadata provider for anime series from Crunchyroll.
/// </summary>
public class CrunchyrollSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly ILogger<CrunchyrollSeriesProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollSeriesProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CrunchyrollSeriesProvider(ILogger<CrunchyrollSeriesProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Crunchyroll";

    /// <inheritdoc />
    public int Order => 3; // After TVDb and TMDb

    /// <inheritdoc />
    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Series>();

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";

        using var httpClient = _httpClientFactory.CreateClient();
        var flareSolverrUrl = config?.FlareSolverrUrl;
        var username = config?.Username;
        var password = config?.Password;
        var dockerContainerName = config?.DockerContainerName;
        var chromeCdpUrl = config?.ChromeCdpUrl;
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale, flareSolverrUrl, username, password, dockerContainerName, chromeCdpUrl);

        string? crunchyrollId = info.GetProviderId("Crunchyroll");
        CrunchyrollSeries? series = null;

        if (!string.IsNullOrEmpty(crunchyrollId))
        {
            _logger.LogDebug("Fetching series by Crunchyroll ID: {Id}", crunchyrollId);
            series = await apiClient.GetSeriesAsync(crunchyrollId, cancellationToken).ConfigureAwait(false);
        }

        if (series == null && !string.IsNullOrEmpty(info.Name))
        {
            _logger.LogDebug("Searching Crunchyroll for series: {Name}", info.Name);
            var searchResults = await apiClient.SearchSeriesAsync(info.Name, 5, cancellationToken).ConfigureAwait(false);
            
            var bestMatch = FindBestMatch(info.Name, searchResults);
            if (bestMatch != null && !string.IsNullOrEmpty(bestMatch.Id))
            {
                series = await apiClient.GetSeriesAsync(bestMatch.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        if (series == null)
        {
            _logger.LogDebug("Failed to retrieve metadata for series: {Name}", info.Name);
            return result;
        }

        result.HasMetadata = true;
        result.Item = new Series
        {
            Name = series.Title,
            Overview = series.ExtendedDescription ?? series.Description,
            ProductionYear = series.SeriesLaunchYear,
            OfficialRating = series.MaturityRatings?.FirstOrDefault()
        };

        // Set provider IDs
        if (!string.IsNullOrEmpty(series.Id))
        {
            result.Item.SetProviderId("Crunchyroll", series.Id);
        }

        // Add genres/tags from keywords
        if (series.Keywords != null)
        {
            foreach (var keyword in series.Keywords.Take(10))
            {
                result.Item.AddGenre(keyword);
            }
        }

        _logger.LogInformation("Successfully retrieved metadata for series: {Name}", series.Title);

        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";

        using var httpClient = _httpClientFactory.CreateClient();
        var flareSolverrUrl = config?.FlareSolverrUrl;
        var username = config?.Username;
        var password = config?.Password;
        var dockerContainerName = config?.DockerContainerName;
        var chromeCdpUrl = config?.ChromeCdpUrl;
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale, flareSolverrUrl, username, password, dockerContainerName, chromeCdpUrl);

        // Check if we have a Crunchyroll ID
        string? crunchyrollId = searchInfo.GetProviderId("Crunchyroll");
        if (!string.IsNullOrEmpty(crunchyrollId))
        {
            var series = await apiClient.GetSeriesAsync(crunchyrollId, cancellationToken).ConfigureAwait(false);
            if (series != null)
            {
                results.Add(CreateSearchResult(series));
                return results;
            }
        }

        // Search by name
        if (!string.IsNullOrEmpty(searchInfo.Name))
        {
            var searchResults = await apiClient.SearchSeriesAsync(searchInfo.Name, 10, cancellationToken).ConfigureAwait(false);
            
            foreach (var item in searchResults)
            {
                results.Add(CreateSearchResultFromItem(item));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
    }

    private static RemoteSearchResult CreateSearchResult(CrunchyrollSeries series)
    {
        var result = new RemoteSearchResult
        {
            Name = series.Title,
            Overview = series.Description,
            ProductionYear = series.SeriesLaunchYear,
            SearchProviderName = "Crunchyroll"
        };

        if (!string.IsNullOrEmpty(series.Id))
        {
            result.SetProviderId("Crunchyroll", series.Id);
        }

        // Get the best poster image
        var posterUrl = GetBestImage(series.Images?.PosterTall);
        if (!string.IsNullOrEmpty(posterUrl))
        {
            result.ImageUrl = posterUrl;
        }

        return result;
    }

    private static RemoteSearchResult CreateSearchResultFromItem(CrunchyrollSearchItem item)
    {
        var result = new RemoteSearchResult
        {
            Name = item.Title,
            Overview = item.Description,
            SearchProviderName = "Crunchyroll"
        };

        if (!string.IsNullOrEmpty(item.Id))
        {
            result.SetProviderId("Crunchyroll", item.Id);
        }

        var posterUrl = GetBestImage(item.Images?.PosterTall);
        if (!string.IsNullOrEmpty(posterUrl))
        {
            result.ImageUrl = posterUrl;
        }

        return result;
    }

    private static string? GetBestImage(List<List<CrunchyrollImage>>? images)
    {
        if (images == null || images.Count == 0)
        {
            return null;
        }

        // Get the largest image from the first set
        var imageSet = images.FirstOrDefault();
        if (imageSet == null || imageSet.Count == 0)
        {
            return null;
        }

        var bestImage = imageSet
            .OrderByDescending(i => i.Width * i.Height)
            .FirstOrDefault();

        return bestImage?.Source;
    }

    private CrunchyrollSearchItem? FindBestMatch(string searchName, List<CrunchyrollSearchItem> results)
    {
        if (results.Count == 0)
        {
            _logger.LogDebug("No results found for search name: {searchName}", searchName);
            return null;
        }

        // Normalize the search name
        var normalizedSearch = NormalizeName(searchName);

        // Try exact match first
        foreach (var result in results)
        {
            if (result.Title != null && NormalizeName(result.Title) == normalizedSearch)
            {
                _logger.LogDebug("Exact match for {searchName} found", searchName);
                return result;
            }
        }

        // Find best partial match
        CrunchyrollSearchItem? bestMatch = null;
        int bestScore = 0;

        foreach (var result in results)
        {
            if (result.Title == null)
            {
                continue;
            }

            int score = CalculateMatchScore(normalizedSearch, NormalizeName(result.Title));
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = result;
            }
        }
        
        int MappingSensitivity = Plugin.Instance?.Configuration.MappingSensitivity ?? 70;
        _logger.LogDebug("Best match for {searchName} found with score: {bestScore}/{MappingSensitivity} for {bestMatch}", searchName, bestScore, MappingSensitivity, bestMatch?.Title ?? "None");
        
        if(bestScore >=  MappingSensitivity){ // Threshold for an acceptable match
            return bestMatch;
        }
        return null;
    }

    private static string NormalizeName(string name)
    {
        return name
            .ToLowerInvariant()
            .Replace(":", "")
            .Replace("-", " ")
            .Replace("  ", " ")
            .Trim();
    }

    private static int CalculateMatchScore(string search, string candidate)
    {
        if (candidate.Contains(search))
        {
            return 100 - (candidate.Length - search.Length);
        }

        if (search.Contains(candidate))
        {
            return 100 - (search.Length - candidate.Length);
        }

        // Word overlap score
        var searchWords = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidateWords = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matchingWords = searchWords.Intersect(candidateWords).Count();

        var similarwords = 0;
        foreach(var word in searchWords.Except(candidateWords).ToArray())
        {
            foreach(var cword in candidateWords.Except(searchWords).ToArray())
            {
                if(cword.Contains(word) || word.Contains(cword))
                {
                    similarwords++;
                    break;
                }
            }
        }

        float score = 100f * (matchingWords + 0.2f * similarwords) / Math.Max(searchWords.Length, candidateWords.Length);
        return (int)score;
    }
}
