using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Jellyfin.Plugin.Crunchyroll.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Api;

/// <summary>
/// HTTP client for communicating with the Crunchyroll API.
/// Supports both direct API access and FlareSolverr for bypassing Cloudflare.
/// When Cloudflare blocks API access, uses FlareSolverr cookies or API proxy as fallback.
/// </summary>
public class CrunchyrollApiClient : IDisposable
{
    private const string BaseUrl = "https://www.crunchyroll.com";
    private const string ApiBaseUrl = "https://www.crunchyroll.com/content/v2";
    
    /// <summary>
    /// Basic authentication token (from crunchyroll-rs Android TV client).
    /// This token is used for OAuth2 authentication with Crunchyroll.
    /// </summary>
    private const string BasicAuthToken = "bmR0aTZicXlqcm9wNXZnZjF0dnU6elpIcS00SEJJVDlDb2FMcnBPREJjRVRCTUNHai1QNlg=";
    
    // Simplified User-Agent (matches Python implementation - less fingerprint surface)
    private const string UserAgent = "Crunchyroll/3.50.2";
    
    private readonly HttpClient _httpClient;
    private readonly HttpClientHandler? _httpClientHandler;
    private readonly ILogger _logger;
    private readonly string _locale;
    private readonly FlareSolverrClient? _flareSolverrClient;
    private readonly string _username;
    private readonly string _password;
    private readonly string _dockerContainerName;
    private readonly string _chromeCdpUrl;
    private bool _disposed;
    private static bool _useScrapingMode;
    private static bool _cfCookiesApplied;
    
    private static string? _accessToken;
    private static string? _refreshToken;  // For token renewal without re-login
    private static DateTime _tokenExpiration = DateTime.MinValue;
    private static readonly SemaphoreSlim _authLock = new(1, 1);
    
    // Rate limits to prevent flooding Cloudflare
    private static DateTime _lastAuthAttempt = DateTime.MinValue;
    private static readonly TimeSpan MinAuthInterval = TimeSpan.FromSeconds(10);
    private static readonly Random _random = new Random();
    
    // Static API response caches (shared across all instances to reduce API calls)
    private static readonly Dictionary<string, List<CrunchyrollSeason>> _apiSeasonsCache = new();
    private static readonly Dictionary<string, List<CrunchyrollEpisode>> _apiEpisodesCache = new();
    private static readonly object _apiCacheLock = new();
    private static DateTime _apiCacheExpiration = DateTime.MinValue;
    private static readonly TimeSpan ApiCacheDuration = TimeSpan.FromMinutes(30);

    // Browser-based authentication token (obtained via FlareSolverr, bypasses Cloudflare)
    private static string? _browserAccessToken;
    private static DateTime _browserTokenExpiration = DateTime.MinValue;
    private static readonly SemaphoreSlim _browserAuthLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollApiClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="locale">The locale for API requests.</param>
    /// <param name="flareSolverrUrl">Optional FlareSolverr URL for bypassing Cloudflare.</param>
    /// <param name="username">Optional username/email for login.</param>
    /// <param name="password">Optional password for login.</param>
    public CrunchyrollApiClient(HttpClient httpClient, ILogger logger, string locale = "pt-BR", string? flareSolverrUrl = null, string? username = null, string? password = null, string? dockerContainerName = null, string? chromeCdpUrl = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _locale = locale;
        _username = username ?? string.Empty;
        _password = password ?? string.Empty;
        _dockerContainerName = dockerContainerName ?? "flaresolverr";
        _chromeCdpUrl = chromeCdpUrl ?? string.Empty;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", locale);

        // Initialize FlareSolverr client if URL is provided
        if (!string.IsNullOrWhiteSpace(flareSolverrUrl))
        {
            _flareSolverrClient = new FlareSolverrClient(new HttpClient(), logger, flareSolverrUrl, chromeCdpUrl);
            _logger.LogDebug("FlareSolverr configured at: {Url}", flareSolverrUrl);
            if (!string.IsNullOrWhiteSpace(chromeCdpUrl))
            {
                _logger.LogInformation("Chrome CDP direct connection configured at: {CdpUrl}", chromeCdpUrl);
            }
        }
    }

    /// <summary>
    /// Creates a CrunchyrollApiClient with a dedicated HttpClientHandler for cookie support.
    /// Use this constructor when Cloudflare bypass via cookies is needed.
    /// </summary>
    public CrunchyrollApiClient(HttpClientHandler handler, ILogger logger, string locale = "pt-BR", string? flareSolverrUrl = null, string? username = null, string? password = null, string? dockerContainerName = null, string? chromeCdpUrl = null)
        : this(new HttpClient(handler, disposeHandler: false), logger, locale, flareSolverrUrl, username, password, dockerContainerName, chromeCdpUrl)
    {
        _httpClientHandler = handler;
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
            if (_useScrapingMode)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiration)
            {
                return true;
            }

            return await TryAuthenticateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Attempts authentication with Crunchyroll (Anonymous or User).
    /// </summary>
    /// <returns>True if authentication succeeded, false if blocked by Cloudflare.</returns>
    private async Task<bool> TryAuthenticateAsync(CancellationToken cancellationToken)
    {
        // Rate Limiting: Don't hammer the auth endpoint
        if (DateTime.UtcNow - _lastAuthAttempt < MinAuthInterval)
        {
            _logger.LogWarning("Skipping authentication attempt (Too many attempts recently). Wait {Seconds}s.", 
                (MinAuthInterval - (DateTime.UtcNow - _lastAuthAttempt)).TotalSeconds);
            return false;
        }

        _lastAuthAttempt = DateTime.UtcNow;

        bool isUserAuth = !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password);
        _logger.LogDebug("Authenticating with Crunchyroll ({Mode})", isUserAuth ? "User" : "Anonymous");

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

            // Prepare authentication request
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/auth/v1/token");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
            // Both flows use the same Basic Token and ETP header
            var deviceId = Guid.NewGuid().ToString();
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicAuthToken);
            request.Headers.Add("ETP-Anonymous-ID", deviceId);
            
            // Determine authentication mode:
            // 1. If we have a refresh token, try to use it first
            // 2. If user credentials provided, use password grant
            // 3. Otherwise, use anonymous client_id grant
            bool useRefreshToken = !string.IsNullOrEmpty(_refreshToken);
            
            if (useRefreshToken)
            {
                _logger.LogDebug("Attempting token refresh with existing refresh_token");
                request.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("refresh_token", _refreshToken!),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("scope", "offline_access"),
                    new KeyValuePair<string, string>("device_id", deviceId)
                });
            }
            else if (isUserAuth)
            {
                request.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("username", _username),
                    new KeyValuePair<string, string>("password", _password),
                    new KeyValuePair<string, string>("grant_type", "password"),
                    new KeyValuePair<string, string>("scope", "offline_access"),
                    new KeyValuePair<string, string>("device_id", deviceId),
                    new KeyValuePair<string, string>("device_type", "com.crunchyroll.android.google")
                });
            }
            else
            {
                request.Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "client_id"),
                    new KeyValuePair<string, string>("device_id", deviceId),
                    new KeyValuePair<string, string>("device_type", "com.crunchyroll.android.google")
                });
            }

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                
                // If refresh token failed, clear it and try again with full login
                if (useRefreshToken && (response.StatusCode == System.Net.HttpStatusCode.BadRequest || 
                                        response.StatusCode == System.Net.HttpStatusCode.Unauthorized))
                {
                    _logger.LogWarning("Refresh token expired or invalid, clearing and retrying with full login");
                    _refreshToken = null;
                    return await TryAuthenticateAsync(cancellationToken).ConfigureAwait(false);
                }
                
                // Check if blocked by Cloudflare (403 Forbidden is the standard indicator)
                // Also handle 429 TooManyRequests which Cloudflare uses for rate limiting bans (Error 1015)
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || 
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("Crunchyroll API authentication returned {StatusCode} (likely Cloudflare block).", response.StatusCode);
                    
                    // Try to bypass Cloudflare using FlareSolverr cookies before giving up
                    if (HasFlareSolverr && !_cfCookiesApplied)
                    {
                        _logger.LogInformation("Attempting Cloudflare bypass via FlareSolverr cookies...");
                        var cfResult = await TryApplyCloudflareCookiesAsync(cancellationToken).ConfigureAwait(false);
                        if (cfResult)
                        {
                            _logger.LogInformation("CF cookies applied. Retrying authentication...");
                            _lastAuthAttempt = DateTime.MinValue; // Reset rate limit for retry
                            return await TryAuthenticateAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                    
                    _logger.LogWarning("Switching to scraping/proxy mode.");
                    _useScrapingMode = true;
                    
                    if (!HasFlareSolverr)
                    {
                        _logger.LogWarning("Cloudflare block detected but FlareSolverr URL is not configured. Please configure FlareSolverr URL in the plugin settings to enable fallback.");
                    }
                    
                    return false;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && errorContent.Contains("invalid_grant"))
                {
                    _logger.LogError("Crunchyroll Login Failed: Invalid Credentials. Please check your Email and Password in the plugin configuration.");
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
            // Save refresh token if provided
            if (!string.IsNullOrEmpty(authResponse.RefreshToken))
            {
                _refreshToken = authResponse.RefreshToken;
            }
            // Set expiration with 60 seconds buffer
            _tokenExpiration = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn - 60);

            _logger.LogInformation("Successfully authenticated with Crunchyroll as {Mode} (Country: {Country})", 
                useRefreshToken ? "Refresh" : (isUserAuth ? "User" : "Anonymous"), authResponse.Country);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Crunchyroll authentication");
            return false;
        }
    }

    /// <summary>
    /// Attempts to obtain and apply Cloudflare bypass cookies from FlareSolverr.
    /// If the plugin and FlareSolverr share the same external IP, these cookies
    /// allow the HttpClient to bypass Cloudflare's 403 blocks.
    /// </summary>
    private async Task<bool> TryApplyCloudflareCookiesAsync(CancellationToken cancellationToken)
    {
        if (_flareSolverrClient == null || !_flareSolverrClient.IsConfigured)
        {
            return false;
        }

        try
        {
            var cookieResult = await _flareSolverrClient.GetCloudflareCookiesAsync(cancellationToken).ConfigureAwait(false);
            if (cookieResult == null)
            {
                _logger.LogWarning("[CF Bypass] Failed to obtain Cloudflare cookies from FlareSolverr");
                return false;
            }

            // Apply cookies and user-agent
            // Note: This only works if the plugin runs on the same machine/IP as FlareSolverr
            if (_httpClientHandler != null)
            {
                FlareSolverrClient.ApplyCloudflareCookies(_httpClientHandler, _httpClient, cookieResult);
                _cfCookiesApplied = true;
                _logger.LogInformation("[CF Bypass] Applied CF cookies (cf_clearance: {HasCf}) and User-Agent to HttpClient",
                    cookieResult.HasCfClearance);
                return true;
            }
            else
            {
                // Can't apply cookies without a handler, but we can change user-agent
                if (!string.IsNullOrEmpty(cookieResult.UserAgent))
                {
                    _httpClient.DefaultRequestHeaders.Remove("User-Agent");
                    _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", cookieResult.UserAgent);
                }
                _logger.LogWarning("[CF Bypass] No HttpClientHandler available to apply cookies. Consider using the handler constructor.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CF Bypass] Error applying Cloudflare cookies");
            return false;
        }
    }

    /// <summary>
    /// Makes an authenticated GET request to the Crunchyroll API.
    /// Handles token expiration (401) by re-authenticating and retrying.
    /// Handles Cloudflare blocks (403) by enabling scraping mode.
    /// </summary>
    private async Task<T?> GetAuthenticatedAsync<T>(string url, CancellationToken cancellationToken)
    {
        var isAuthenticated = await TryEnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
        
        if (!isAuthenticated)
        {
            return default;
        }

        // Add random jitter to behave more like a human client and avoid rate limits (0.5s to 1.5s)
        await Task.Delay(_random.Next(500, 1500), cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Handle Token Expiration / Invalidation
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Crunchyroll API returned Unauthorized (401). clearing token and retrying authentication...");
            
            // Clear static token to force re-auth
            _accessToken = null;
            _tokenExpiration = DateTime.MinValue;

            // Re-authenticate
            isAuthenticated = await TryEnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            if (!isAuthenticated)
            {
                return default;
            }

            // Retry request with new token
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            response = await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
        }
        
        // Handle Cloudflare Block
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Crunchyroll API request Forbidden (403). Likely Cloudflare block. Switching to scraping mode for future requests.");
            _useScrapingMode = true;
            return default;
        }
        
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

            // Fall back to CDP proxy when Cloudflare blocks the API
            if (_useScrapingMode && HasFlareSolverr)
            {
                // CDP proxy: search via the real API through Chrome
                var proxyResults = await TrySearchViaApiProxyAsync(encodedQuery, limit, cancellationToken).ConfigureAwait(false);
                if (proxyResults != null && proxyResults.Count > 0)
                {
                    _logger.LogInformation("[CDP Proxy] Search for '{Query}' returned {Count} results", query, proxyResults.Count);
                    return proxyResults;
                }

                // Last resort: HTML scraping (very limited on modern Crunchyroll SPA)
                _logger.LogWarning("[Fallback] CDP proxy search failed for '{Query}'. Trying HTML scraping (limited)...", query);
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

            // Fall back to CDP proxy when Cloudflare blocks the API
            if (_useScrapingMode && HasFlareSolverr)
            {
                // CDP proxy: fetches from the real API via Chrome inside FlareSolverr
                // Returns complete series data including all image variants
                var proxyResult = await TryGetSeriesViaApiProxyAsync(seriesId, cancellationToken).ConfigureAwait(false);
                if (proxyResult != null)
                {
                    _logger.LogInformation("[CDP Proxy] Got series {SeriesId}: {Title} (PosterTall={HasTall}, PosterWide={HasWide})",
                        seriesId,
                        proxyResult.Title,
                        proxyResult.Images?.PosterTall?.Count > 0,
                        proxyResult.Images?.PosterWide?.Count > 0);
                    return proxyResult;
                }

                // Last resort: HTML scraping (very limited on modern Crunchyroll SPA)
                // Only gets title from og:title and a single poster from og:image
                _logger.LogWarning("[Fallback] CDP proxy failed for series {SeriesId}. Trying HTML scraping (limited data)...", seriesId);
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

    // Static scraping caches (shared across all instances to avoid redundant FlareSolverr requests)
    private static readonly Dictionary<string, List<CrunchyrollEpisode>> _scrapedEpisodesCache = new();
    private static readonly Dictionary<string, List<CrunchyrollSeason>> _scrapedSeasonsCache = new();
    private static readonly object _scrapingCacheLock = new();

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
                // Check API cache first (thread-safe)
                lock (_apiCacheLock)
                {
                    if (DateTime.UtcNow < _apiCacheExpiration && 
                        _apiSeasonsCache.TryGetValue(seriesId, out var cachedSeasons) && 
                        cachedSeasons.Count > 0)
                    {
                        _logger.LogDebug("[API Cache] Hit for seasons of series {SeriesId}", seriesId);
                        return cachedSeasons;
                    }
                }

                var url = $"{ApiBaseUrl}/cms/series/{seriesId}/seasons?locale={_locale}";
                
                _logger.LogDebug("Fetching seasons for series: {SeriesId}", seriesId);
                
                var response = await GetAuthenticatedAsync<CrunchyrollResponse<CrunchyrollSeason>>(url, cancellationToken)
                    .ConfigureAwait(false);

                // If successful (response is not null), cache and return data.
                if (response?.Data != null && response.Data.Count > 0)
                {
                    lock (_apiCacheLock)
                    {
                        _apiSeasonsCache[seriesId] = response.Data;
                        _apiCacheExpiration = DateTime.UtcNow.Add(ApiCacheDuration);
                    }
                    _logger.LogDebug("[API Cache] Stored {Count} seasons for series {SeriesId}", response.Data.Count, seriesId);
                    return response.Data;
                }
            }

            // Fall back to CDP proxy when Cloudflare blocks the API
            if (_useScrapingMode)
            {
                if (!HasFlareSolverr)
                {
                    _logger.LogWarning("Cannot fetch seasons: FlareSolverr is not configured and API is blocked by Cloudflare.");
                    return new List<CrunchyrollSeason>();
                }

                // Check cache first (thread-safe)
                lock (_scrapingCacheLock)
                {
                    if (_scrapedSeasonsCache.TryGetValue(seriesId, out var cachedSeasons) && cachedSeasons.Count > 0)
                    {
                        _logger.LogDebug("[Cache] Hit for seasons of series {SeriesId}, returning {Count} seasons", seriesId, cachedSeasons.Count);
                        return cachedSeasons;
                    }
                }

                // CDP proxy: fetches from the real API via Chrome inside FlareSolverr
                // Returns complete season data with all metadata
                var apiProxySeasonsResult = await TryGetSeasonsViaApiProxyAsync(seriesId, cancellationToken).ConfigureAwait(false);
                if (apiProxySeasonsResult != null && apiProxySeasonsResult.Count > 0)
                {
                    lock (_scrapingCacheLock)
                    {
                        _scrapedSeasonsCache[seriesId] = apiProxySeasonsResult;
                    }
                    return apiProxySeasonsResult;
                }

                // CDP DOM extraction: navigate to the page in Chrome, wait for React to render
                // the episode cards, then extract data directly from the DOM via JavaScript.
                // This works even when the CDP proxy API calls fail, because the browser's
                // own JavaScript loads the episodes client-side.
                _logger.LogInformation("[CDP DOM] CDP proxy API failed. Trying DOM extraction for series {SeriesId}...", seriesId);
                var domResult = await TryGetSeasonsViaDomExtractionAsync(seriesId, cancellationToken).ConfigureAwait(false);
                if (domResult != null && domResult.Count > 0)
                {
                    lock (_scrapingCacheLock)
                    {
                        _scrapedSeasonsCache[seriesId] = domResult;
                    }
                    return domResult;
                }

                _logger.LogWarning("[Fallback] All strategies failed for series {SeriesId}.", seriesId);
            }

            _logger.LogWarning("No seasons found for series: {SeriesId}", seriesId);
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
            // If in scraping mode, try cache first, then CDP proxy
            if (_useScrapingMode)
            {
                lock (_scrapingCacheLock)
                {
                    if (_scrapedEpisodesCache.TryGetValue(seasonId, out var cachedEpisodes))
                    {
                        _logger.LogInformation("[Scraping Cache] Hit for episodes of season {SeasonId}, returning {Count} episodes", seasonId, cachedEpisodes.Count);
                        return cachedEpisodes;
                    }
                }

                // Cache miss — try fetching episodes via CDP API proxy (same as seasons do)
                if (HasFlareSolverr)
                {
                    _logger.LogInformation("[CDP Proxy] Fetching episodes for season {SeasonId} (scraping mode, cache miss)", seasonId);
                    var proxyResult = await TryGetEpisodesViaApiProxyAsync(seasonId, cancellationToken).ConfigureAwait(false);
                    if (proxyResult != null && proxyResult.Count > 0)
                    {
                        lock (_scrapingCacheLock)
                        {
                            _scrapedEpisodesCache[seasonId] = proxyResult;
                        }
                        _logger.LogInformation("[CDP Proxy] Got {Count} episodes for season {SeasonId}", proxyResult.Count, seasonId);
                        return proxyResult;
                    }
                }

                _logger.LogWarning("No episodes found for season {SeasonId} via cache or CDP proxy.", seasonId);
                return new List<CrunchyrollEpisode>();
            }

            // Check API cache first (thread-safe)
            lock (_apiCacheLock)
            {
                if (DateTime.UtcNow < _apiCacheExpiration && 
                    _apiEpisodesCache.TryGetValue(seasonId, out var cachedEpisodes) && 
                    cachedEpisodes.Count > 0)
                {
                    _logger.LogDebug("[API Cache] Hit for episodes of season {SeasonId}", seasonId);
                    return cachedEpisodes;
                }
            }

            var url = $"{ApiBaseUrl}/cms/seasons/{seasonId}/episodes?locale={_locale}";
            
            _logger.LogDebug("Fetching episodes for season: {SeasonId}", seasonId);
            
            var response = await GetAuthenticatedAsync<CrunchyrollResponse<CrunchyrollEpisode>>(url, cancellationToken)
                .ConfigureAwait(false);

            if (response?.Data != null && response.Data.Count > 0)
            {
                lock (_apiCacheLock)
                {
                    _apiEpisodesCache[seasonId] = response.Data;
                    _apiCacheExpiration = DateTime.UtcNow.Add(ApiCacheDuration);
                }
                _logger.LogDebug("[API Cache] Stored {Count} episodes for season {SeasonId}", response.Data.Count, seasonId);
                return response.Data;
            }

            // If API failed during this call (switched to scraping), try FlareSolverr API proxy
            if (_useScrapingMode && HasFlareSolverr)
            {
                var proxyResult = await TryGetEpisodesViaApiProxyAsync(seasonId, cancellationToken).ConfigureAwait(false);
                if (proxyResult != null && proxyResult.Count > 0)
                {
                    lock (_apiCacheLock)
                    {
                        _apiEpisodesCache[seasonId] = proxyResult;
                    }
                    return proxyResult;
                }
                
                // Also check scraping cache as last resort
                lock (_scrapingCacheLock)
                {
                    if (_scrapedEpisodesCache.TryGetValue(seasonId, out var cachedEpisodes))
                    {
                        _logger.LogInformation("[Scraping Cache] Hit for episodes of season {SeasonId}", seasonId);
                        return cachedEpisodes;
                    }
                }
                
                _logger.LogWarning("Switched to scraping mode during GetEpisodes. No cached or proxied data available for season {SeasonId}.", seasonId);
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
    /// Gets episodes for a series by fetching all seasons and their episodes.
    /// Uses CDP proxy when API is blocked by Cloudflare.
    /// </summary>
    /// <param name="seriesId">The Crunchyroll series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of episodes from all seasons.</returns>
    public async Task<List<CrunchyrollEpisode>> GetEpisodesBySeriesIdAsync(string seriesId, CancellationToken cancellationToken = default)
    {
        try
        {
            var allEpisodes = new List<CrunchyrollEpisode>();
            var seasons = await GetSeasonsAsync(seriesId, cancellationToken).ConfigureAwait(false);
            
            foreach (var season in seasons)
            {
                if (string.IsNullOrEmpty(season.Id))
                {
                    continue;
                }

                var episodes = await GetEpisodesAsync(season.Id, cancellationToken).ConfigureAwait(false);
                allEpisodes.AddRange(episodes);
            }

            return allEpisodes;
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

                var episode = response?.Data?.FirstOrDefault();
                if (episode != null)
                {
                    return episode;
                }
            }

            // Fall back to CDP proxy if in scraping mode (needed for episode thumbnails)
            if (_useScrapingMode && HasFlareSolverr)
            {
                var proxyResult = await TryGetEpisodeViaApiProxyAsync(episodeId, cancellationToken).ConfigureAwait(false);
                if (proxyResult != null)
                {
                    _logger.LogInformation("[CDP Proxy] Got episode {EpisodeId}: {Title} (HasThumbnail={HasThumb})",
                        episodeId,
                        proxyResult.Title,
                        proxyResult.Images?.Thumbnail?.Count > 0);
                    return proxyResult;
                }
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

    /// <summary>
    /// Authenticates with Crunchyroll through FlareSolverr's browser session.
    /// FlareSolverr's Chrome browser bypasses Cloudflare's TLS fingerprinting,
    /// allowing us to POST to the auth endpoint with custom headers.
    /// The browser session already has Cloudflare cookies from the initial page visit.
    /// </summary>
    /// <returns>A valid Bearer access token, or null if authentication failed.</returns>
    private async Task<string?> TryAuthenticateViaFlareSolverrAsync(CancellationToken cancellationToken)
    {
        if (_flareSolverrClient == null || !_flareSolverrClient.IsConfigured)
        {
            return null;
        }

        // Check cached browser token
        if (!string.IsNullOrEmpty(_browserAccessToken) && DateTime.UtcNow < _browserTokenExpiration)
        {
            return _browserAccessToken;
        }

        await _browserAuthLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after lock
            if (!string.IsNullOrEmpty(_browserAccessToken) && DateTime.UtcNow < _browserTokenExpiration)
            {
                return _browserAccessToken;
            }

            _logger.LogInformation("[CDP Auth] Authenticating via Chrome DevTools Protocol...");

            // Authenticate by executing fetch() inside Chrome's browser context via CDP.
            // This bypasses Cloudflare's TLS fingerprinting because Chrome makes the request
            // directly, not our .NET HttpClient. The token obtained is a standard JWT
            // that can be used for subsequent API calls (also via CDP).
            var deviceId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Basic {BasicAuthToken}",
                ["Content-Type"] = "application/x-www-form-urlencoded"
            };
            var body = $"grant_type=client_id&device_id={deviceId}";

            var json = await _flareSolverrClient.CdpFetchJsonAsync(
                _dockerContainerName,
                "/auth/v1/token",
                "POST",
                headers,
                body,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("[CDP Auth] Empty response from CDP auth. Check Docker container '{Container}' is running.",
                    _dockerContainerName);
                return null;
            }

            // Parse the auth response
            var authResponse = JsonSerializer.Deserialize<CrunchyrollAuthResponse>(json);
            if (authResponse == null || string.IsNullOrEmpty(authResponse.AccessToken))
            {
                _logger.LogWarning("[CDP Auth] Invalid auth response: {Json}",
                    json.Length > 200 ? json[..200] : json);
                return null;
            }

            _browserAccessToken = authResponse.AccessToken;
            _browserTokenExpiration = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn - 60);
            _logger.LogInformation("[CDP Auth] Successfully authenticated! Country: {Country}, Token: {TokenLen} chars",
                authResponse.Country, authResponse.AccessToken.Length);

            return _browserAccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CDP Auth] Error during authentication");
            return null;
        }
        finally
        {
            _browserAuthLock.Release();
        }
    }

    /// <summary>
    /// Core helper: authenticates via CDP, calls the API, and automatically retries once
    /// if the token was expired/rejected. This eliminates the "token expired mid-operation"
    /// problem where individual calls would fail permanently without retry.
    /// </summary>
    /// <param name="apiPath">The Crunchyroll API path (e.g. /content/v2/cms/series/{id}).</param>
    /// <param name="operationName">Human-readable label for logging (e.g. "seasons", "episodes").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSON response string, or null on failure.</returns>
    private async Task<string?> CdpFetchWithAuthRetryAsync(string apiPath, string operationName, CancellationToken cancellationToken)
    {
        if (_flareSolverrClient == null)
        {
            return null;
        }

        const int maxAttempts = 2; // 1 normal + 1 retry after re-auth

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Step 1: Get (or refresh) the authenticated token
            var token = await TryAuthenticateViaFlareSolverrAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogDebug("[CDP Proxy] Cannot authenticate for {Operation}", operationName);
                return null;
            }

            // Step 2: Call the API with Bearer token via CDP fetch()
            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token}"
            };

            if (attempt == 1)
            {
                _logger.LogInformation("[CDP Proxy] Fetching {Operation}: {Path}", operationName, apiPath);
            }
            else
            {
                _logger.LogInformation("[CDP Proxy] Retry #{Attempt} for {Operation} with fresh token", attempt, operationName);
            }

            var json = await _flareSolverrClient.CdpFetchJsonAsync(
                _dockerContainerName, apiPath, "GET", headers, null, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogDebug("[CDP Proxy] No response for {Operation}", operationName);
                return null;
            }

            // Step 3: Check for auth errors — if token expired, invalidate and retry
            if (json.Contains("invalid_auth_token") || json.Contains("unauthorized"))
            {
                _logger.LogWarning("[CDP Proxy] Token rejected for {Operation} (attempt {Attempt}/{Max}). Invalidating token.",
                    operationName, attempt, maxAttempts);
                _browserAccessToken = null;
                _browserTokenExpiration = DateTime.MinValue;

                if (attempt < maxAttempts)
                {
                    // Will loop back, TryAuthenticateViaFlareSolverrAsync will issue a fresh token
                    continue;
                }

                _logger.LogError("[CDP Proxy] Token still rejected after re-auth for {Operation}. Giving up.", operationName);
                return null;
            }

            // Success — return the JSON
            return json;
        }

        return null;
    }

    /// <summary>
    /// Tries to fetch seasons for a series through CDP (Chrome DevTools Protocol).
    /// Flow: 1) Authenticate via CDP fetch → get Bearer token
    ///       2) Fetch seasons API via CDP fetch with Bearer header → get JSON data
    /// All requests execute inside Chrome in FlareSolverr, bypassing Cloudflare's TLS fingerprinting.
    /// If the token has expired, re-authenticates and retries once automatically.
    /// </summary>
    private async Task<List<CrunchyrollSeason>?> TryGetSeasonsViaApiProxyAsync(string seriesId, CancellationToken cancellationToken)
    {
        try
        {
            var apiPath = $"/content/v2/cms/series/{seriesId}/seasons?locale={_locale}";
            var json = await CdpFetchWithAuthRetryAsync(apiPath, $"seasons/{seriesId}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<CrunchyrollResponse<CrunchyrollSeason>>(json);
            if (response?.Data != null && response.Data.Count > 0)
            {
                _logger.LogInformation("[CDP Proxy] Got {Count} seasons for series {SeriesId}",
                    response.Data.Count, seriesId);
                foreach (var s in response.Data)
                {
                    _logger.LogInformation(
                        "[CDP Proxy] Season: Id={Id}, Title={Title}, SeasonNumber={SeasonNumber}, SeasonSequenceNumber={SeqNum}, AudioLocales={Audio}, NumberOfEpisodes={EpCount}",
                        s.Id, s.Title, s.SeasonNumber, s.SeasonSequenceNumber,
                        s.AudioLocales != null ? string.Join(",", s.AudioLocales) : "null",
                        s.NumberOfEpisodes);
                }
                return response.Data;
            }

            _logger.LogDebug("[CDP Proxy] No season data in response for series {SeriesId}", seriesId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Proxy] Failed to fetch seasons for series {SeriesId}", seriesId);
        }

        return null;
    }

    /// <summary>
    /// Tries to fetch episodes for a season through CDP (Chrome DevTools Protocol).
    /// Same approach as TryGetSeasonsViaApiProxyAsync - uses CDP-based auth + CDP fetch.
    /// Automatically retries once on token expiration.
    /// </summary>
    private async Task<List<CrunchyrollEpisode>?> TryGetEpisodesViaApiProxyAsync(string seasonId, CancellationToken cancellationToken)
    {
        try
        {
            var apiPath = $"/content/v2/cms/seasons/{seasonId}/episodes?locale={_locale}";
            var json = await CdpFetchWithAuthRetryAsync(apiPath, $"episodes/{seasonId}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<CrunchyrollResponse<CrunchyrollEpisode>>(json);
            if (response?.Data != null && response.Data.Count > 0)
            {
                _logger.LogInformation("[CDP Proxy] Got {Count} episodes for season {SeasonId}",
                    response.Data.Count, seasonId);
                return response.Data;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Proxy] Failed to fetch episodes for season {SeasonId}", seasonId);
        }

        return null;
    }

    /// <summary>
    /// Tries to fetch series details through CDP (Chrome DevTools Protocol).
    /// This returns the full series object including PosterTall and PosterWide images,
    /// which are essential for the Jellyfin image provider.
    /// Automatically retries once on token expiration.
    /// </summary>
    private async Task<CrunchyrollSeries?> TryGetSeriesViaApiProxyAsync(string seriesId, CancellationToken cancellationToken)
    {
        try
        {
            var apiPath = $"/content/v2/cms/series/{seriesId}?locale={_locale}";
            var json = await CdpFetchWithAuthRetryAsync(apiPath, $"series/{seriesId}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<CrunchyrollResponse<CrunchyrollSeries>>(json);
            var series = response?.Data?.FirstOrDefault();
            if (series != null)
            {
                _logger.LogInformation("[CDP Proxy] Got series {SeriesId}: {Title}",
                    seriesId, series.Title);
                return series;
            }

            _logger.LogDebug("[CDP Proxy] No series data in response for {SeriesId}", seriesId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Proxy] Failed to fetch series {SeriesId}", seriesId);
        }

        return null;
    }

    /// <summary>
    /// Tries to fetch a single episode through CDP (Chrome DevTools Protocol).
    /// This returns the full episode object including thumbnail images,
    /// which are essential for the Jellyfin episode image provider.
    /// Automatically retries once on token expiration.
    /// </summary>
    private async Task<CrunchyrollEpisode?> TryGetEpisodeViaApiProxyAsync(string episodeId, CancellationToken cancellationToken)
    {
        try
        {
            var apiPath = $"/content/v2/cms/episodes/{episodeId}?locale={_locale}";
            var json = await CdpFetchWithAuthRetryAsync(apiPath, $"episode/{episodeId}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<CrunchyrollResponse<CrunchyrollEpisode>>(json);
            var episode = response?.Data?.FirstOrDefault();
            if (episode != null)
            {
                _logger.LogInformation("[CDP Proxy] Got episode {EpisodeId}: {Title} (HasThumbnail={HasThumb})",
                    episodeId, episode.Title,
                    episode.Images?.Thumbnail?.Count > 0);
                return episode;
            }

            _logger.LogDebug("[CDP Proxy] No episode data in response for {EpisodeId}", episodeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Proxy] Failed to fetch episode {EpisodeId}", episodeId);
        }

        return null;
    }

    /// <summary>
    /// Tries to search for series through CDP (Chrome DevTools Protocol).
    /// This uses the real search API via Chrome, returning complete results with images.
    /// Automatically retries once on token expiration.
    /// </summary>
    private async Task<List<CrunchyrollSearchItem>?> TrySearchViaApiProxyAsync(string encodedQuery, int limit, CancellationToken cancellationToken)
    {
        try
        {
            var apiPath = $"/content/v2/discover/search?q={encodedQuery}&n={limit}&type=series&locale={_locale}";
            var json = await CdpFetchWithAuthRetryAsync(apiPath, $"search/{HttpUtility.UrlDecode(encodedQuery)}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<CrunchyrollSearchResponse>(json);
            if (response?.Data != null && response.Data.Count > 0)
            {
                var seriesResults = response.Data.FirstOrDefault(d => d.Type == "series");
                var items = seriesResults?.Items ?? new List<CrunchyrollSearchItem>();
                _logger.LogInformation("[CDP Proxy] Search returned {Count} results", items.Count);
                return items;
            }

            _logger.LogDebug("[CDP Proxy] No search results in response");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Proxy] Failed to search for '{Query}'", HttpUtility.UrlDecode(encodedQuery));
        }

        return null;
    }

    /// <summary>
    /// Extracts season/episode data by navigating Chrome to the series page and waiting for
    /// the SPA to render. The browser's own JavaScript loads episode data via client-side
    /// API calls, so we just wait for the DOM to be populated and then extract the data.
    /// This works as a last resort when both direct API and CDP proxy API calls fail.
    /// </summary>
    private async Task<List<CrunchyrollSeason>?> TryGetSeasonsViaDomExtractionAsync(string seriesId, CancellationToken cancellationToken)
    {
        if (_flareSolverrClient == null)
        {
            return null;
        }

        try
        {
            var seriesUrl = $"{BaseUrl}/series/{seriesId}";
            var json = await _flareSolverrClient.CdpExtractRenderedEpisodesAsync(
                _dockerContainerName, seriesUrl, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogDebug("[CDP DOM] No data from DOM extraction");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var waitedMs = root.TryGetProperty("waitedMs", out var waitProp) ? waitProp.GetInt32() : 0;
            var cardCount = root.TryGetProperty("cardCount", out var cardProp) ? cardProp.GetInt32() : 0;
            _logger.LogInformation("[CDP DOM] Page rendered in {WaitMs}ms, found {CardCount} watch links", waitedMs, cardCount);

            // Extract episodes from the DOM data
            var episodes = new List<CrunchyrollEpisode>();
            if (root.TryGetProperty("episodes", out var episodesArray))
            {
                int seqNum = 0;
                foreach (var epEl in episodesArray.EnumerateArray())
                {
                    var ep = new CrunchyrollEpisode
                    {
                        Id = epEl.TryGetProperty("id", out var idProp) ? idProp.GetString() : null,
                        Title = epEl.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null,
                        EpisodeNumber = epEl.TryGetProperty("episodeNumber", out var numProp) ? numProp.GetString() : null,
                        SequenceNumber = seqNum++
                    };

                    // Parse duration (e.g., "23m" → ms)
                    if (epEl.TryGetProperty("duration", out var durProp))
                    {
                        var durStr = durProp.GetString();
                        if (durStr != null)
                        {
                            var durMatch = System.Text.RegularExpressions.Regex.Match(durStr, @"(\d+)");
                            if (durMatch.Success && int.TryParse(durMatch.Groups[1].Value, out var minutes))
                            {
                                ep.DurationMs = minutes * 60 * 1000;
                            }
                        }
                    }

                    // Set thumbnail if available
                    if (epEl.TryGetProperty("thumbnail", out var thumbProp))
                    {
                        var thumbUrl = thumbProp.GetString();
                        if (!string.IsNullOrEmpty(thumbUrl))
                        {
                            ep.Images = new CrunchyrollEpisodeImages
                            {
                                Thumbnail = new List<List<CrunchyrollImage>>
                                {
                                    new List<CrunchyrollImage>
                                    {
                                        new CrunchyrollImage
                                        {
                                            Source = thumbUrl,
                                            Width = 640,
                                            Height = 360,
                                            Type = "thumbnail"
                                        }
                                    }
                                }
                            };
                        }
                    }

                    if (!string.IsNullOrEmpty(ep.Id))
                    {
                        episodes.Add(ep);
                    }
                }
            }

            if (episodes.Count == 0)
            {
                _logger.LogWarning("[CDP DOM] No episodes extracted from rendered page. SPA might not have finished loading.");
                return null;
            }

            // Build season info from DOM data
            string seasonId;
            string seasonTitle;

            if (root.TryGetProperty("series", out var seriesEl) &&
                seriesEl.TryGetProperty("currentSeasonId", out var seasonIdProp) &&
                !string.IsNullOrEmpty(seasonIdProp.GetString()))
            {
                seasonId = seasonIdProp.GetString()!;
                seasonTitle = seriesEl.TryGetProperty("seasonTitle", out var stProp) ? stProp.GetString() ?? "Season 1" : "Season 1";
            }
            else
            {
                seasonId = $"{seriesId}_dom";
                seasonTitle = "Season 1 (DOM)";
            }

            var season = new CrunchyrollSeason
            {
                Id = seasonId,
                Title = seasonTitle,
                SeasonNumber = 1,
                SeriesId = seriesId
            };

            // Cache the episodes
            lock (_scrapingCacheLock)
            {
                _scrapedEpisodesCache[seasonId] = episodes;
            }

            _logger.LogInformation("[CDP DOM] Extracted {Count} episodes for series {SeriesId} (season: {SeasonId})",
                episodes.Count, seriesId, seasonId);

            return new List<CrunchyrollSeason> { season };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP DOM] Failed to extract data from rendered page for series {SeriesId}", seriesId);
        }

        return null;
    }

    /// <summary>
    /// Searches for a related series (e.g., "Frieren Season 2") when the current series
    /// doesn't have enough seasons to match the Jellyfin library.
    /// Some anime have separate Crunchyroll series entries for each season.
    /// </summary>
    /// <param name="seriesTitle">The base series title.</param>
    /// <param name="seasonNumber">The season number to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The related series ID, or null if not found.</returns>
    public async Task<string?> FindRelatedSeriesIdAsync(string seriesTitle, int seasonNumber, CancellationToken cancellationToken = default)
    {
        if (seasonNumber <= 1 || string.IsNullOrEmpty(seriesTitle))
        {
            return null;
        }

        try
        {
            // Search variations: "Title Season 2", "Title 2nd Season", "Title Part 2"
            var searchQueries = new[]
            {
                $"{seriesTitle} Season {seasonNumber}",
                $"{seriesTitle} {seasonNumber}",
            };

            foreach (var query in searchQueries)
            {
                _logger.LogDebug("[RelatedSeries] Searching for: {Query}", query);
                var results = await SearchSeriesAsync(query, 5, cancellationToken).ConfigureAwait(false);

                foreach (var result in results)
                {
                    if (string.IsNullOrEmpty(result.Title) || string.IsNullOrEmpty(result.Id))
                    {
                        continue;
                    }

                    // Check if the result title contains the season number indicator
                    var resultTitle = result.Title.ToLowerInvariant();
                    var baseTitle = seriesTitle.ToLowerInvariant();

                    // Match patterns like "Title Season 2", "Title 2nd Season", "Title Part 2"
                    bool isMatch = resultTitle.Contains($"season {seasonNumber}") ||
                                   resultTitle.Contains($"part {seasonNumber}") ||
                                   resultTitle.Contains($"{seasonNumber}nd season") ||
                                   resultTitle.Contains($"{seasonNumber}rd season") ||
                                   resultTitle.Contains($"{seasonNumber}th season") ||
                                   resultTitle.Contains($"{seasonNumber}st season");

                    // Also check that the base title is similar
                    bool titleSimilar = resultTitle.Contains(baseTitle) ||
                                        baseTitle.Contains(resultTitle.Replace($" season {seasonNumber}", "").Trim());

                    if (isMatch && titleSimilar)
                    {
                        _logger.LogInformation("[RelatedSeries] Found related series for S{Season}: {Title} (ID: {Id})",
                            seasonNumber, result.Title, result.Id);
                        return result.Id;
                    }
                }
            }

            _logger.LogDebug("[RelatedSeries] No related series found for '{Title}' Season {Season}", seriesTitle, seasonNumber);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RelatedSeries] Error searching for related series");
            return null;
        }
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
            // Static lock should not be disposed here
            // _authLock.Dispose();
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
