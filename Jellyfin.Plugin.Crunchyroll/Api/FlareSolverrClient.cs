using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Api;

/// <summary>
/// Client for FlareSolverr proxy to bypass Cloudflare protection.
/// </summary>
public class FlareSolverrClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _flareSolverrUrl;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlareSolverrClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="flareSolverrUrl">The FlareSolverr base URL (e.g., http://localhost:8191).</param>
    public FlareSolverrClient(HttpClient httpClient, ILogger logger, string flareSolverrUrl)
    {
        _httpClient = httpClient;
        _logger = logger;
        _flareSolverrUrl = flareSolverrUrl.TrimEnd('/');
    }

    /// <summary>
    /// Gets a value indicating whether FlareSolverr is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_flareSolverrUrl);

    /// <summary>
    /// Fetches a URL through FlareSolverr, bypassing Cloudflare protection.
    /// </summary>
    /// <param name="url">The URL to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTML content of the page.</returns>
    public async Task<string?> GetPageContentAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("FlareSolverr is not configured");
            return null;
        }

        try
        {
            var request = new FlareSolverrRequest
            {
                Cmd = "request.get",
                Url = url,
                MaxTimeout = 60000
            };

            _logger.LogInformation("[FlareSolverr] Requesting URL: {Url}", url);

            var response = await _httpClient.PostAsJsonAsync(
                $"{_flareSolverrUrl}/v1",
                request,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("FlareSolverr request failed with status {StatusCode}: {Error}",
                    response.StatusCode, errorContent);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result == null)
            {
                _logger.LogError("Failed to deserialize FlareSolverr response");
                return null;
            }

            if (result.Status != "ok")
            {
                _logger.LogError("FlareSolverr returned error status: {Status} - {Message}",
                    result.Status, result.Message);
                return null;
            }

            var htmlLength = result.Solution?.Response?.Length ?? 0;
            var solutionStatus = result.Solution?.Status ?? 0;
            _logger.LogInformation("[FlareSolverr] SUCCESS - Status: {SolutionStatus}, HTML length: {Length} chars", solutionStatus, htmlLength);
            
            if (htmlLength == 0)
            {
                _logger.LogWarning("[FlareSolverr] Response HTML is empty!");
            }
            else if (htmlLength < 1000)
            {
                _logger.LogWarning("[FlareSolverr] Response HTML is very short ({Length} chars), might be an error page", htmlLength);
            }
            
            return result.Solution?.Response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching URL through FlareSolverr: {Url}", url);
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
    /// Disposes resources.
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
            _httpClient.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>
/// FlareSolverr request model.
/// </summary>
public class FlareSolverrRequest
{
    /// <summary>
    /// Gets or sets the command (e.g., "request.get").
    /// </summary>
    [JsonPropertyName("cmd")]
    public string Cmd { get; set; } = "request.get";

    /// <summary>
    /// Gets or sets the URL to fetch.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the max timeout in milliseconds.
    /// </summary>
    [JsonPropertyName("maxTimeout")]
    public int MaxTimeout { get; set; } = 60000;

    /// <summary>
    /// Gets or sets the time to wait for JavaScript to load (in milliseconds).
    /// This allows dynamic content to render before capturing the page.
    /// </summary>
    [JsonPropertyName("wait")]
    public int Wait { get; set; } = 3000;
}

/// <summary>
/// FlareSolverr response model.
/// </summary>
public class FlareSolverrResponse
{
    /// <summary>
    /// Gets or sets the status ("ok" or "error").
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets the error message if any.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the solution containing the response.
    /// </summary>
    [JsonPropertyName("solution")]
    public FlareSolverrSolution? Solution { get; set; }
}

/// <summary>
/// FlareSolverr solution containing the actual response.
/// </summary>
public class FlareSolverrSolution
{
    /// <summary>
    /// Gets or sets the URL that was fetched.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the HTTP status code.
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>
    /// Gets or sets the response HTML content.
    /// </summary>
    [JsonPropertyName("response")]
    public string? Response { get; set; }

    /// <summary>
    /// Gets or sets the cookies set by the page.
    /// </summary>
    [JsonPropertyName("cookies")]
    public List<FlareSolverrCookie>? Cookies { get; set; }

    /// <summary>
    /// Gets or sets the user agent used.
    /// </summary>
    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }
}

/// <summary>
/// FlareSolverr cookie model.
/// </summary>
public class FlareSolverrCookie
{
    /// <summary>
    /// Gets or sets the cookie name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the cookie value.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>
    /// Gets or sets the cookie domain.
    /// </summary>
    [JsonPropertyName("domain")]
    public string? Domain { get; set; }
}
