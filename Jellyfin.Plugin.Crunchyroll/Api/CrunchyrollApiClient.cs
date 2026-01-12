using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Web;
using Jellyfin.Plugin.Crunchyroll.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Api;

/// <summary>
/// HTTP client for communicating with the Crunchyroll API.
/// Supports both direct API access and FlareSolverr for bypassing Cloudflare.
/// </summary>
public class CrunchyrollApiClient : IDisposable
{
    private const string BaseUrl = "https://www.crunchyroll.com";
    private const string ApiBaseUrl = "https://www.crunchyroll.com/content/v2";
    
    /// <summary>
    /// Basic authentication token for anonymous access (base64 of "cr_web:").
    /// </summary>
    private const string AnonymousAuthToken = "Y3Jfd2ViOg==";
    
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _locale;
    private readonly FlareSolverrClient? _flareSolverrClient;
    private bool _disposed;
    private bool _useScrapingMode;
    
    private string? _accessToken;
    private DateTime _tokenExpiration = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="locale">The locale for API requests.</param>
    /// <param name="flareSolverrUrl">Optional FlareSolverr URL for bypassing Cloudflare.</param>
    public CrunchyrollApiClient(HttpClient httpClient, ILogger logger, string locale = "pt-BR", string? flareSolverrUrl = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _locale = locale;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", locale);

        // Initialize FlareSolverr client if URL is provided
        if (!string.IsNullOrWhiteSpace(flareSolverrUrl))
        {
            _flareSolverrClient = new FlareSolverrClient(new HttpClient(), logger, flareSolverrUrl);
            _logger.LogInformation("FlareSolverr configured at: {Url}", flareSolverrUrl);
        }
    }

    /// <summary>
    /// Gets a value indicating whether FlareSolverr is available.
    /// </summary>
    public bool HasFlareSolverr => _flareSolverrClient?.IsConfigured ?? false;

    /// <summary>
    /// Ensures we have a valid access token, obtaining one if necessary.
    /// </summary>
    private async Task<bool> TryEnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_useScrapingMode)
        {
            return false; // Skip API auth when in scraping mode
        }

        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiration)
        {
            return true;
        }

        await _authLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiration)
            {
                return true;
            }

            return await TryAuthenticateAnonymousAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Attempts anonymous authentication with Crunchyroll.
    /// </summary>
    /// <returns>True if authentication succeeded, false if blocked by Cloudflare.</returns>
    private async Task<bool> TryAuthenticateAnonymousAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Authenticating with Crunchyroll (anonymous)");

        try
        {
            // First, make an initial call to the Crunchyroll page to avoid bot detection
            try
            {
                var initialResponse = await _httpClient.GetAsync(BaseUrl, cancellationToken).ConfigureAwait(false);
                if (!initialResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Initial Crunchyroll request returned {StatusCode}", initialResponse.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to make initial Crunchyroll request");
            }

            // Now perform the anonymous authentication
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth/v1/token");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", AnonymousAuthToken);
            
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_id")
            });

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                
                // Check if blocked by Cloudflare
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && 
                    errorContent.Contains("cloudflare", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Crunchyroll API blocked by Cloudflare. Switching to scraping mode.");
                    _useScrapingMode = true;
                    return false;
                }
                
                _logger.LogError("Crunchyroll authentication failed with status {StatusCode}: {Error}", 
                    response.StatusCode, errorContent);
                return false;
            }

            var authResponse = await response.Content.ReadFromJsonAsync<CrunchyrollAuthResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (authResponse == null)
            {
                _logger.LogError("Failed to deserialize Crunchyroll auth response");
                return false;
            }

            _accessToken = authResponse.AccessToken;
            // Set expiration with 60 seconds buffer
            _tokenExpiration = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn - 60);

            _logger.LogDebug("Successfully authenticated with Crunchyroll (country: {Country})", authResponse.Country);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Crunchyroll authentication");
            return false;
        }
    }

    /// <summary>
    /// Makes an authenticated GET request to the Crunchyroll API.
    /// </summary>
    private async Task<T?> GetAuthenticatedAsync<T>(string url, CancellationToken cancellationToken)
    {
        var isAuthenticated = await TryEnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        
        if (!isAuthenticated)
        {
            return default;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("Crunchyroll API request to {Url} failed with status {StatusCode}: {Error}", 
                url, response.StatusCode, errorContent);
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches a page through FlareSolverr and scrapes the content.
    /// </summary>
    private async Task<string?> GetPageViaFlareSolverrAsync(string url, CancellationToken cancellationToken)
    {
        if (_flareSolverrClient == null || !_flareSolverrClient.IsConfigured)
        {
            _logger.LogWarning("FlareSolverr not configured, cannot fetch page: {Url}", url);
            return null;
        }

        return await _flareSolverrClient.GetPageContentAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for anime series on Crunchyroll.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of search results.</returns>
    public async Task<List<CrunchyrollSearchItem>> SearchSeriesAsync(string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        try
        {
            var encodedQuery = HttpUtility.UrlEncode(query);
            
            // Try API first if not in scraping mode
            if (!_useScrapingMode)
            {
                var url = $"{BaseUrl}/content/v2/discover/search?q={encodedQuery}&n={limit}&type=series&locale={_locale}";
                
                _logger.LogDebug("Searching Crunchyroll for: {Query}", query);
                
                var response = await GetAuthenticatedAsync<CrunchyrollSearchResponse>(url, cancellationToken)
                    .ConfigureAwait(false);

                if (response?.Data != null && response.Data.Count > 0)
                {
                    var seriesResults = response.Data.FirstOrDefault(d => d.Type == "series");
                    return seriesResults?.Items ?? new List<CrunchyrollSearchItem>();
                }
            }

            // Fall back to scraping if API failed or in scraping mode
            if (_useScrapingMode && HasFlareSolverr)
            {
                var searchUrl = $"{BaseUrl}/{_locale.ToLowerInvariant()}/search?q={encodedQuery}";
                var html = await GetPageViaFlareSolverrAsync(searchUrl, cancellationToken).ConfigureAwait(false);
                
                if (!string.IsNullOrEmpty(html))
                {
                    return CrunchyrollHtmlScraper.ExtractSearchResultsFromHtml(html, _logger);
                }
            }

            _logger.LogDebug("No results found for: {Query}", query);
            return new List<CrunchyrollSearchItem>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching Crunchyroll for: {Query}", query);
            return new List<CrunchyrollSearchItem>();
        }
    }

    /// <summary>
    /// Gets detailed information about a series.
    /// </summary>
    /// <param name="seriesId">The Crunchyroll series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The series information or null if not found.</returns>
    public async Task<CrunchyrollSeries?> GetSeriesAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Try API first if not in scraping mode
            if (!_useScrapingMode)
            {
                var url = $"{ApiBaseUrl}/cms/series/{seriesId}?locale={_locale}";
                
                _logger.LogDebug("Fetching series: {SeriesId}", seriesId);
                
                var response = await GetAuthenticatedAsync<CrunchyrollResponse<CrunchyrollSeries>>(url, cancellationToken)
                    .ConfigureAwait(false);

                var series = response?.Data?.FirstOrDefault();
                if (series != null)
                {
                    return series;
                }
            }

            // Fall back to scraping if API failed or in scraping mode
            if (_useScrapingMode && HasFlareSolverr)
            {
                var pageUrl = $"{BaseUrl}/{_locale.ToLowerInvariant()}/series/{seriesId}";
                var html = await GetPageViaFlareSolverrAsync(pageUrl, cancellationToken).ConfigureAwait(false);
                
                if (!string.IsNullOrEmpty(html))
                {
                    return CrunchyrollHtmlScraper.ExtractSeriesFromHtml(html, seriesId, _logger);
                }
            }

            _logger.LogDebug("Series not found: {SeriesId}", seriesId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching series: {SeriesId}", seriesId);
            return null;
        }
    }

    /// <summary>
    /// Gets all seasons for a series.
    /// </summary>
    /// <param name="seriesId">The Crunchyroll series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of seasons.</returns>
    public async Task<List<CrunchyrollSeason>> GetSeasonsAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Try API first if not in scraping mode
            if (!_useScrapingMode)
            {
                var url = $"{ApiBaseUrl}/cms/series/{seriesId}/seasons?locale={_locale}";
                
                _logger.LogDebug("Fetching seasons for series: {SeriesId}", seriesId);
                
                var response = await GetAuthenticatedAsync<CrunchyrollResponse<CrunchyrollSeason>>(url, cancellationToken)
                    .ConfigureAwait(false);

                if (response?.Data != null)
                {
                    return response.Data;
                }
            }

            // Scraping for seasons is more complex - would need to parse dropdown
            // For now, return empty list if API fails
            _logger.LogDebug("No seasons found for series: {SeriesId}", seriesId);
            return new List<CrunchyrollSeason>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching seasons for series: {SeriesId}", seriesId);
            return new List<CrunchyrollSeason>();
        }
    }

    /// <summary>
    /// Gets all episodes for a season.
    /// </summary>
    /// <param name="seasonId">The Crunchyroll season ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of episodes.</returns>
    public async Task<List<CrunchyrollEpisode>> GetEpisodesAsync(string seasonId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Try API first if not in scraping mode
            if (!_useScrapingMode)
            {
                var url = $"{ApiBaseUrl}/cms/seasons/{seasonId}/episodes?locale={_locale}";
                
                _logger.LogDebug("Fetching episodes for season: {SeasonId}", seasonId);
                
                var response = await GetAuthenticatedAsync<CrunchyrollResponse<CrunchyrollEpisode>>(url, cancellationToken)
                    .ConfigureAwait(false);

                if (response?.Data != null)
                {
                    return response.Data;
                }
            }

            _logger.LogDebug("No episodes found for season: {SeasonId}", seasonId);
            return new List<CrunchyrollEpisode>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching episodes for season: {SeasonId}", seasonId);
            return new List<CrunchyrollEpisode>();
        }
    }

    /// <summary>
    /// Gets episodes for a series by scraping the series page.
    /// This is useful when API is blocked and we have FlareSolverr.
    /// </summary>
    /// <param name="seriesId">The Crunchyroll series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of episodes from the first/default season.</returns>
    public async Task<List<CrunchyrollEpisode>> GetEpisodesBySeriesIdAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!HasFlareSolverr)
            {
                _logger.LogWarning("Cannot get episodes by series ID without FlareSolverr");
                return new List<CrunchyrollEpisode>();
            }

            var pageUrl = $"{BaseUrl}/{_locale.ToLowerInvariant()}/series/{seriesId}";
            var html = await GetPageViaFlareSolverrAsync(pageUrl, cancellationToken).ConfigureAwait(false);
            
            if (!string.IsNullOrEmpty(html))
            {
                return CrunchyrollHtmlScraper.ExtractEpisodesFromHtml(html, _logger);
            }

            return new List<CrunchyrollEpisode>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching episodes for series: {SeriesId}", seriesId);
            return new List<CrunchyrollEpisode>();
        }
    }

    /// <summary>
    /// Gets a specific episode by ID.
    /// </summary>
    /// <param name="episodeId">The Crunchyroll episode ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The episode information or null if not found.</returns>
    public async Task<CrunchyrollEpisode?> GetEpisodeAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Try API first if not in scraping mode
            if (!_useScrapingMode)
            {
                var url = $"{ApiBaseUrl}/cms/episodes/{episodeId}?locale={_locale}";
                
                _logger.LogDebug("Fetching episode: {EpisodeId}", episodeId);
                
                var response = await GetAuthenticatedAsync<CrunchyrollResponse<CrunchyrollEpisode>>(url, cancellationToken)
                    .ConfigureAwait(false);

                return response?.Data?.FirstOrDefault();
            }

            _logger.LogDebug("Episode not found: {EpisodeId}", episodeId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching episode: {EpisodeId}", episodeId);
            return null;
        }
    }

    /// <summary>
    /// Builds a full Crunchyroll URL for a series.
    /// </summary>
    /// <param name="slugTitle">The slug title of the series.</param>
    /// <returns>The full URL.</returns>
    public static string BuildSeriesUrl(string slugTitle)
    {
        return $"{BaseUrl}/series/{slugTitle}";
    }

    /// <summary>
    /// Builds a full Crunchyroll URL for an episode.
    /// </summary>
    /// <param name="episodeId">The episode ID.</param>
    /// <param name="slugTitle">The slug title of the episode.</param>
    /// <returns>The full URL.</returns>
    public static string BuildEpisodeUrl(string episodeId, string slugTitle)
    {
        return $"{BaseUrl}/watch/{episodeId}/{slugTitle}";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes of resources.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _authLock.Dispose();
            _httpClient.Dispose();
            _flareSolverrClient?.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>
/// Wrapper for search response.
/// </summary>
public class CrunchyrollSearchResponse
{
    /// <summary>
    /// Gets or sets the search result data.
    /// </summary>
    public List<CrunchyrollSearchResult>? Data { get; set; }
}
