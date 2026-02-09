using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Api;

/// <summary>
/// Client for FlareSolverr proxy to bypass Cloudflare protection.
/// Supports session persistence, cookie extraction, and API proxying.
/// </summary>
public class FlareSolverrClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _flareSolverrUrl;
    private readonly string? _chromeCdpUrl;
    private bool _disposed;

    // Session management for persistent browser instances
    private string? _sessionId;
    private static readonly SemaphoreSlim _sessionLock = new(1, 1);

    // Cached Cloudflare cookies and user-agent from last successful request
    private static string? _cachedUserAgent;
    private static List<FlareSolverrCookie>? _cachedCookies;
    private static DateTime _cookieCacheExpiration = DateTime.MinValue;
    private static readonly TimeSpan CookieCacheDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Initializes a new instance of the <see cref="FlareSolverrClient"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="flareSolverrUrl">The FlareSolverr base URL (e.g., http://localhost:8191).</param>
    /// <param name="chromeCdpUrl">Optional Chrome CDP URL for direct WebSocket connection (e.g., http://localhost:9222).</param>
    public FlareSolverrClient(HttpClient httpClient, ILogger logger, string flareSolverrUrl, string? chromeCdpUrl = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _flareSolverrUrl = flareSolverrUrl.TrimEnd('/');
        _chromeCdpUrl = string.IsNullOrWhiteSpace(chromeCdpUrl) ? null : chromeCdpUrl.TrimEnd('/');
    }

    /// <summary>
    /// Gets a value indicating whether FlareSolverr is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_flareSolverrUrl);

    /// <summary>
    /// Gets a value indicating whether direct Chrome CDP WebSocket connection is configured.
    /// </summary>
    public bool HasDirectCdp => !string.IsNullOrWhiteSpace(_chromeCdpUrl);

    // Track whether we have an active FlareSolverr session keeping Chrome alive for CDP
    private static bool _cdpSessionActive;
    private static string? _cdpSessionId;
    private static readonly SemaphoreSlim _cdpSessionLock = new(1, 1);

    /// <summary>
    /// Ensures Chrome is running inside FlareSolverr by creating a persistent session.
    /// FlareSolverr v3.x kills Chrome after each request unless a session is active.
    /// This method creates a session (which starts Chrome) and navigates to crunchyroll.com
    /// so that subsequent CDP calls can find Chrome and execute fetch() on the right origin.
    /// </summary>
    private async Task EnsureBrowserAliveAsync(CancellationToken cancellationToken)
    {
        if (_cdpSessionActive && _cdpSessionId != null)
        {
            return; // Session already exists, Chrome should be running
        }

        await _cdpSessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cdpSessionActive && _cdpSessionId != null)
            {
                return; // Double-check after lock
            }

            _cdpSessionId = $"cdp_{Guid.NewGuid():N}"[..16];
            _logger.LogInformation("[CDP Session] Creating FlareSolverr session '{SessionId}' to keep Chrome alive...", _cdpSessionId);

            // Create session by making a request with session parameter.
            // This starts Chrome and keeps it alive for the duration of the session.
            var request = new FlareSolverrRequest
            {
                Cmd = "request.get",
                Url = "https://www.crunchyroll.com/",
                Session = _cdpSessionId,
                MaxTimeout = 60000,
                Wait = 5000
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_flareSolverrUrl}/v1", request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _cdpSessionActive = true;
                _logger.LogInformation("[CDP Session] Session created. Chrome is now running and on crunchyroll.com.");
            }
            else
            {
                _logger.LogWarning("[CDP Session] Failed to create session: HTTP {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Session] Error creating browser session");
        }
        finally
        {
            _cdpSessionLock.Release();
        }
    }

    /// <summary>
    /// Obtains Cloudflare bypass cookies and user-agent by visiting Crunchyroll through FlareSolverr.
    /// These can then be applied to an HttpClient for direct API access.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple of (UserAgent, Cookies) or null if failed.</returns>
    public async Task<CloudflareCookieResult?> GetCloudflareCookiesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        // Check cache first
        if (_cachedUserAgent != null && _cachedCookies != null && DateTime.UtcNow < _cookieCacheExpiration)
        {
            _logger.LogDebug("[FlareSolverr] Using cached CF cookies (expires in {Minutes}m)",
                (_cookieCacheExpiration - DateTime.UtcNow).TotalMinutes);
            return new CloudflareCookieResult
            {
                UserAgent = _cachedUserAgent,
                Cookies = _cachedCookies
            };
        }

        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check cache after lock
            if (_cachedUserAgent != null && _cachedCookies != null && DateTime.UtcNow < _cookieCacheExpiration)
            {
                return new CloudflareCookieResult
                {
                    UserAgent = _cachedUserAgent,
                    Cookies = _cachedCookies
                };
            }

            _logger.LogInformation("[FlareSolverr] Obtaining Cloudflare cookies via page visit...");

            var sessionId = GetOrCreateSession();

            var request = new FlareSolverrRequest
            {
                Cmd = "request.get",
                Url = "https://www.crunchyroll.com/",
                MaxTimeout = 60000,
                Wait = 5000, // Only need to wait for CF challenge, not full page render
                Session = sessionId
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_flareSolverrUrl}/v1",
                request,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FlareSolverr] Failed to get CF cookies: HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result?.Status != "ok" || result.Solution == null)
            {
                _logger.LogWarning("[FlareSolverr] Failed to get CF cookies: {Status} - {Message}",
                    result?.Status, result?.Message);
                return null;
            }

            var userAgent = result.Solution.UserAgent;
            var cookies = result.Solution.Cookies ?? new List<FlareSolverrCookie>();

            // Check if we got cf_clearance
            var cfClearance = cookies.FirstOrDefault(c => c.Name == "cf_clearance");
            if (cfClearance == null)
            {
                _logger.LogWarning("[FlareSolverr] No cf_clearance cookie obtained. Cloudflare may not be active.");
            }
            else
            {
                _logger.LogInformation("[FlareSolverr] Got cf_clearance cookie. Caching for {Minutes}m.", CookieCacheDuration.TotalMinutes);
            }

            // Cache the results
            _cachedUserAgent = userAgent;
            _cachedCookies = cookies;
            _cookieCacheExpiration = DateTime.UtcNow.Add(CookieCacheDuration);

            return new CloudflareCookieResult
            {
                UserAgent = userAgent ?? string.Empty,
                Cookies = cookies
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FlareSolverr] Error obtaining CF cookies");
            return null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Applies Cloudflare cookies and user-agent to an HttpClient handler.
    /// This allows the HttpClient to bypass Cloudflare using cookies obtained by FlareSolverr.
    /// </summary>
    /// <param name="handler">The HttpClientHandler to configure.</param>
    /// <param name="httpClient">The HttpClient to set user-agent on.</param>
    /// <param name="cookieResult">The cookie result from GetCloudflareCookiesAsync.</param>
    public static void ApplyCloudflareCookies(HttpClientHandler handler, HttpClient httpClient, CloudflareCookieResult cookieResult)
    {
        if (handler.CookieContainer == null)
        {
            handler.CookieContainer = new CookieContainer();
        }

        foreach (var cookie in cookieResult.Cookies)
        {
            if (string.IsNullOrEmpty(cookie.Name) || string.IsNullOrEmpty(cookie.Value))
            {
                continue;
            }

            try
            {
                var domain = cookie.Domain ?? ".crunchyroll.com";
                handler.CookieContainer.Add(new Cookie(cookie.Name, cookie.Value, "/", domain));
            }
            catch
            {
                // Skip invalid cookies
            }
        }

        // Set matching user-agent (critical for cf_clearance validation)
        if (!string.IsNullOrEmpty(cookieResult.UserAgent))
        {
            httpClient.DefaultRequestHeaders.Remove("User-Agent");
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", cookieResult.UserAgent);
        }
    }

    /// <summary>
    /// Fetches a URL through FlareSolverr, bypassing Cloudflare protection.
    /// Uses session for persistent browser state.
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
            var sessionId = GetOrCreateSession();

            var request = new FlareSolverrRequest
            {
                Cmd = "request.get",
                Url = url,
                MaxTimeout = 120000,
                Wait = 15000, // 15s for JS rendering
                Session = sessionId
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

            // Update cached cookies from this response
            if (result.Solution?.Cookies != null && result.Solution.Cookies.Count > 0)
            {
                _cachedCookies = result.Solution.Cookies;
                _cachedUserAgent = result.Solution.UserAgent ?? _cachedUserAgent;
                _cookieCacheExpiration = DateTime.UtcNow.Add(CookieCacheDuration);
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

    /// <summary>
    /// Makes a POST request through FlareSolverr's browser with custom headers and body.
    /// Used for authentication endpoints that require specific headers (e.g., Basic auth).
    /// The FlareSolverr browser session handles Cloudflare bypass transparently.
    /// </summary>
    /// <param name="url">The URL to POST to.</param>
    /// <param name="postData">The POST body (e.g., form-urlencoded data).</param>
    /// <param name="headers">Optional custom headers (e.g., Authorization).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSON response body, or null if failed.</returns>
    public async Task<string?> PostViaFlareSolverrAsync(
        string url,
        string postData,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            var sessionId = GetOrCreateSession();

            var request = new FlareSolverrRequest
            {
                Cmd = "request.post",
                Url = url,
                MaxTimeout = 60000,
                Wait = 3000,
                Session = sessionId,
                PostData = postData,
                Headers = headers
            };

            _logger.LogDebug("[FlareSolverr] POST via browser: {Url}", url);

            var response = await _httpClient.PostAsJsonAsync(
                $"{_flareSolverrUrl}/v1",
                request,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FlareSolverr] POST request failed with HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result?.Status != "ok" || result.Solution?.Response == null)
            {
                _logger.LogWarning("[FlareSolverr] POST returned error: {Status} - {Message}",
                    result?.Status, result?.Message);
                return null;
            }

            // Update cached cookies from this response
            if (result.Solution?.Cookies != null && result.Solution.Cookies.Count > 0)
            {
                _cachedCookies = result.Solution.Cookies;
                _cachedUserAgent = result.Solution.UserAgent ?? _cachedUserAgent;
                _cookieCacheExpiration = DateTime.UtcNow.Add(CookieCacheDuration);
            }

            _logger.LogDebug("[FlareSolverr] POST response status: {SolutionStatus}, length: {Length}",
                result.Solution.Status, result.Solution.Response.Length);

            return ExtractJsonFromResponse(result.Solution.Response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FlareSolverr] Error POSTing via browser: {Url}", url);
            return null;
        }
    }

    /// <summary>
    /// Makes a GET request to a Crunchyroll API endpoint through FlareSolverr.
    /// The FlareSolverr browser session already has CF cookies, so API requests bypass Cloudflare.
    /// Supports custom headers for authenticated API access (Bearer token).
    /// </summary>
    /// <param name="apiUrl">The full API URL to request.</param>
    /// <param name="headers">Optional custom headers (e.g., Authorization: Bearer).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSON string response, or null if failed.</returns>
    public async Task<string?> GetApiJsonAsync(string apiUrl, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            var sessionId = GetOrCreateSession();

            var request = new FlareSolverrRequest
            {
                Cmd = "request.get",
                Url = apiUrl,
                MaxTimeout = 60000,
                Wait = 2000,
                Session = sessionId,
                Headers = headers
            };

            _logger.LogDebug("[FlareSolverr] API GET: {Url}", apiUrl);

            var response = await _httpClient.PostAsJsonAsync(
                $"{_flareSolverrUrl}/v1",
                request,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[FlareSolverr] API GET failed with HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<FlareSolverrResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result?.Status != "ok" || result.Solution?.Response == null)
            {
                _logger.LogWarning("[FlareSolverr] API GET returned error: {Status} - {Message}",
                    result?.Status, result?.Message);
                return null;
            }

            return ExtractJsonFromResponse(result.Solution.Response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FlareSolverr] Error proxying API request: {Url}", apiUrl);
            return null;
        }
    }

    /// <summary>
    /// Extracts JSON content from FlareSolverr's HTML-wrapped response.
    /// Chrome renders raw JSON inside a &lt;pre&gt; tag. This method handles
    /// both pre-wrapped JSON and raw JSON responses, with HTML entity decoding.
    /// </summary>
    private string? ExtractJsonFromResponse(string html)
    {
        // Pattern 1: JSON wrapped in <pre> tag (Chrome's default JSON rendering)
        var preMatch = Regex.Match(html, @"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline);
        if (preMatch.Success)
        {
            var content = preMatch.Groups[1].Value;
            // Decode HTML entities (e.g., &amp; → &, &quot; → ")
            content = WebUtility.HtmlDecode(content);
            return content;
        }

        // Pattern 2: Raw JSON (no HTML wrapping)
        var trimmed = html.Trim();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        _logger.LogWarning("[FlareSolverr] Response was not JSON. Length: {Length}", html.Length);
        return null;
    }

    // Python script that runs inside FlareSolverr container via docker exec.
    // It discovers Chrome's CDP port, connects via websocket, and executes
    // a fetch() call to Crunchyroll's auth endpoint from the browser context.
    // This bypasses both Cloudflare TLS fingerprinting and API authentication.
    private const string CdpAuthScript = @"
import json,re,glob,sys
port=None
for p in glob.glob('/proc/*/cmdline'):
    try:
        with open(p,'rb') as f:
            data=f.read().decode('utf-8',errors='ignore')
        m=re.search(r'--remote-debugging-port=(\d+)',data)
        if m:
            port=int(m.group(1))
            break
    except:
        pass
if not port:
    print(json.dumps({'error':'no_chrome_port'}))
    sys.exit(0)
import urllib.request
try:
    pages=json.loads(urllib.request.urlopen(f'http://127.0.0.1:{port}/json/list',timeout=5).read())
except:
    print(json.dumps({'error':'cdp_connect_failed','port':port}))
    sys.exit(0)
ws_url=None
for p in pages:
    if 'crunchyroll' in p.get('url',''):
        ws_url=p.get('webSocketDebuggerUrl')
        break
if not ws_url and pages:
    ws_url=pages[0].get('webSocketDebuggerUrl')
if not ws_url:
    print(json.dumps({'error':'no_pages'}))
    sys.exit(0)
import websocket
ws=websocket.create_connection(ws_url,suppress_origin=True,timeout=30)
ws.send(json.dumps({'id':1,'method':'Runtime.evaluate','params':{'expression':'window.location.hostname','returnByValue':True}}))
r=json.loads(ws.recv())
h=r.get('result',{}).get('result',{}).get('value','')
if 'crunchyroll' not in h:
    ws.send(json.dumps({'id':2,'method':'Page.navigate','params':{'url':'https://www.crunchyroll.com/'}}))
    ws.recv()
    import time
    time.sleep(5)
js=""""""(async()=>{try{const r=await fetch('/auth/v1/token',{method:'POST',headers:{'Authorization':'Basic bmR0aTZicXlqcm9wNXZnZjF0dnU6elpIcS00SEJJVDlDb2FMcnBPREJjRVRCTUNHai1QNlg=','Content-Type':'application/x-www-form-urlencoded'},body:'grant_type=client_id&device_id='+crypto.randomUUID()});const d=await r.json();return JSON.stringify({access_token:d.access_token,token_type:d.token_type,expires_in:d.expires_in,country:d.country})}catch(e){return JSON.stringify({error:e.message})}})()""""""
ws.send(json.dumps({'id':3,'method':'Runtime.evaluate','params':{'expression':js,'awaitPromise':True,'returnByValue':True}}))
r=json.loads(ws.recv())
v=r.get('result',{}).get('result',{}).get('value','{}')
ws.close()
print(v)
";

    /// <summary>
    /// Obtains a Crunchyroll API access token by executing a CDP (Chrome DevTools Protocol)
    /// script inside the FlareSolverr Docker container. The script connects to Chrome's
    /// internal CDP port and executes a fetch() call to the auth endpoint from the browser
    /// context, bypassing both Cloudflare TLS fingerprinting and API authentication.
    /// </summary>
    /// <param name="dockerContainerName">The Docker container name/ID of FlareSolverr.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A CdpAuthResult with the access token, or null if failed.</returns>
    public async Task<CdpAuthResult?> GetAuthTokenViaCdpAsync(string dockerContainerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dockerContainerName))
        {
            _logger.LogDebug("[CDP Auth] Docker container name not configured");
            return null;
        }

        try
        {
            _logger.LogInformation("[CDP Auth] Getting auth token via CDP from container '{Container}'...", dockerContainerName);

            // Base64 encode the Python script to avoid shell escaping issues
            var scriptBytes = Encoding.UTF8.GetBytes(CdpAuthScript.Trim());
            var encodedScript = Convert.ToBase64String(scriptBytes);

            // Build docker exec command
            var pythonCmd = $"import base64; exec(base64.b64decode('{encodedScript}'))";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "exec", dockerContainerName, "python3", "-c", pythonCmd },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            // Wait for process to exit (with timeout)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[CDP Auth] Docker exec timed out after 60s");
                try { process.Kill(); } catch { /* ignore */ }
                return null;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("[CDP Auth] Docker exec failed with exit code {ExitCode}. Stderr: {Stderr}",
                    process.ExitCode, stderr.Length > 500 ? stderr.Substring(0, 500) : stderr);
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning("[CDP Auth] Empty output from CDP script. Stderr: {Stderr}", stderr);
                return null;
            }

            // Parse the JSON output
            var result = JsonSerializer.Deserialize<CdpAuthResult>(stdout.Trim());
            if (result == null)
            {
                _logger.LogWarning("[CDP Auth] Failed to parse output: {Output}", stdout.Trim());
                return null;
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogWarning("[CDP Auth] Script error: {Error}", result.Error);
                return null;
            }

            if (string.IsNullOrEmpty(result.AccessToken))
            {
                _logger.LogWarning("[CDP Auth] No access_token in response: {Output}", stdout.Trim());
                return null;
            }

            _logger.LogInformation("[CDP Auth] Successfully obtained token via CDP! Country: {Country}, Expires: {Expires}s",
                result.Country, result.ExpiresIn);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[CDP Auth] Error running CDP auth script. Is Docker accessible and container '{Container}' running?",
                dockerContainerName);
            return null;
        }
    }

    // ===== Direct WebSocket CDP Methods =====
    // These methods connect directly to Chrome's CDP WebSocket from C#,
    // eliminating the need for docker exec + Python. Requires the Chrome CDP
    // port to be accessible from the Jellyfin host (e.g., FlareSolverr with --network=host).

    /// <summary>
    /// CDP page info returned by the /json/list endpoint.
    /// </summary>
    private sealed class CdpPageInfo
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("webSocketDebuggerUrl")]
        public string? WebSocketDebuggerUrl { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    /// <summary>
    /// Executes a JavaScript expression inside Chrome via direct WebSocket CDP connection.
    /// This bypasses docker exec entirely — connects directly to Chrome's CDP port.
    /// </summary>
    /// <param name="jsExpression">The JavaScript expression to evaluate (async IIFE returning a string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The string result from the JS expression, or null if failed.</returns>
    private async Task<string?> ExecuteJsViaCdpDirectAsync(string jsExpression, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_chromeCdpUrl))
        {
            return null;
        }

        try
        {
            _logger.LogDebug("[CDP Direct] Connecting to Chrome CDP at {Url}...", _chromeCdpUrl);

            // Step 1: Discover pages via CDP HTTP endpoint
            string pagesJson;
            try
            {
                pagesJson = await _httpClient.GetStringAsync($"{_chromeCdpUrl}/json/list", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CDP Direct] Cannot reach Chrome CDP at {Url}/json/list", _chromeCdpUrl);
                return null;
            }

            var pages = JsonSerializer.Deserialize<List<CdpPageInfo>>(pagesJson);
            if (pages == null || pages.Count == 0)
            {
                _logger.LogWarning("[CDP Direct] No pages found at {Url}", _chromeCdpUrl);
                return null;
            }

            // Find a page with crunchyroll in the URL, or use the first page
            string? wsUrl = null;
            foreach (var page in pages)
            {
                if (page.Url?.Contains("crunchyroll", StringComparison.OrdinalIgnoreCase) == true
                    && !string.IsNullOrEmpty(page.WebSocketDebuggerUrl))
                {
                    wsUrl = page.WebSocketDebuggerUrl;
                    _logger.LogDebug("[CDP Direct] Found Crunchyroll page: {Url}", page.Url);
                    break;
                }
            }

            if (wsUrl == null)
            {
                var firstPage = pages.FirstOrDefault(p => !string.IsNullOrEmpty(p.WebSocketDebuggerUrl));
                wsUrl = firstPage?.WebSocketDebuggerUrl;
                if (firstPage != null)
                {
                    _logger.LogDebug("[CDP Direct] Using first available page: {Url}", firstPage.Url);
                }
            }

            if (string.IsNullOrEmpty(wsUrl))
            {
                _logger.LogWarning("[CDP Direct] No webSocketDebuggerUrl found in any page");
                return null;
            }

            // Replace internal 127.0.0.1 address with the external CDP host
            var cdpUri = new Uri(_chromeCdpUrl);
            wsUrl = Regex.Replace(wsUrl, @"ws://[^/]+", $"ws://{cdpUri.Host}:{cdpUri.Port}");

            _logger.LogDebug("[CDP Direct] Connecting to WebSocket: {WsUrl}", wsUrl);

            // Step 2: Connect via WebSocket
            using var ws = new ClientWebSocket();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await ws.ConnectAsync(new Uri(wsUrl), connectCts.Token).ConfigureAwait(false);

            int msgId = 1;

            // Step 3: Check if we're on crunchyroll.com
            var hostname = await CdpEvaluateStringAsync(ws, "window.location.hostname", msgId++, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(hostname) || !hostname.Contains("crunchyroll", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[CDP Direct] Not on crunchyroll.com (hostname={Host}), navigating...", hostname);

                // Navigate to crunchyroll.com
                var navMsg = JsonSerializer.Serialize(new
                {
                    id = msgId++,
                    method = "Page.navigate",
                    @params = new { url = "https://www.crunchyroll.com/" }
                });
                await CdpSendAsync(ws, navMsg, cancellationToken).ConfigureAwait(false);
                await CdpReceiveResponseAsync(ws, msgId - 1, cancellationToken).ConfigureAwait(false);

                // Wait for page to load
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
            }

            // Step 4: Execute the JavaScript expression
            _logger.LogDebug("[CDP Direct] Evaluating JS expression ({Length} chars)", jsExpression.Length);
            var result = await CdpEvaluateStringAsync(ws, jsExpression, msgId++, cancellationToken, awaitPromise: true).ConfigureAwait(false);

            // Step 5: Close cleanly
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore close errors
            }

            if (string.IsNullOrEmpty(result))
            {
                _logger.LogWarning("[CDP Direct] Empty result from JS evaluation");
                return null;
            }

            _logger.LogDebug("[CDP Direct] Got result ({Length} chars)", result.Length);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CDP Direct] Error connecting to Chrome CDP at {Url}", _chromeCdpUrl);
            return null;
        }
    }

    /// <summary>
    /// Sends a Runtime.evaluate command and returns the string value of the result.
    /// </summary>
    private async Task<string?> CdpEvaluateStringAsync(
        ClientWebSocket ws,
        string expression,
        int msgId,
        CancellationToken cancellationToken,
        bool awaitPromise = false)
    {
        var msg = JsonSerializer.Serialize(new
        {
            id = msgId,
            method = "Runtime.evaluate",
            @params = new
            {
                expression,
                returnByValue = true,
                awaitPromise
            }
        });

        await CdpSendAsync(ws, msg, cancellationToken).ConfigureAwait(false);
        var response = await CdpReceiveResponseAsync(ws, msgId, cancellationToken).ConfigureAwait(false);

        // Parse response: {"id":N,"result":{"result":{"type":"string","value":"..."}}}
        try
        {
            using var doc = JsonDocument.Parse(response);

            // Check for errors
            if (doc.RootElement.TryGetProperty("result", out var resultProp))
            {
                if (resultProp.TryGetProperty("exceptionDetails", out var exDetails))
                {
                    _logger.LogWarning("[CDP Direct] JS exception: {Details}",
                        exDetails.GetRawText().Length > 300 ? exDetails.GetRawText()[..300] : exDetails.GetRawText());
                    return null;
                }

                if (resultProp.TryGetProperty("result", out var innerResult))
                {
                    if (innerResult.TryGetProperty("value", out var valueProp))
                    {
                        return valueProp.ValueKind == JsonValueKind.String
                            ? valueProp.GetString()
                            : valueProp.GetRawText();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[CDP Direct] Could not parse evaluate response: {Response}",
                response.Length > 200 ? response[..200] : response);
        }

        return null;
    }

    /// <summary>
    /// Sends a message over the CDP WebSocket.
    /// </summary>
    private static async Task CdpSendAsync(ClientWebSocket ws, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Receives a single complete WebSocket message.
    /// </summary>
    private static async Task<string> CdpReceiveMessageAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return sb.ToString();
    }

    /// <summary>
    /// Receives CDP messages until one with the expected command ID is found.
    /// Discards any events or responses with different IDs.
    /// </summary>
    private async Task<string> CdpReceiveResponseAsync(ClientWebSocket ws, int expectedId, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        while (true)
        {
            var message = await CdpReceiveMessageAsync(ws, timeoutCts.Token).ConfigureAwait(false);

            try
            {
                using var doc = JsonDocument.Parse(message);
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetInt32() == expectedId)
                {
                    return message;
                }

                // It's an event or response to a different command — skip
            }
            catch
            {
                // Not valid JSON — skip
            }
        }
    }

    // Generic Python script for executing arbitrary JavaScript via Chrome DevTools Protocol.
    // Accepts the JS expression as a base64-encoded command-line argument (sys.argv[1]).
    // The script discovers Chrome's CDP port, connects via websocket, ensures we're on
    // crunchyroll.com, then evaluates the provided JS expression and prints the result.
    private const string CdpFetchScript = @"
import json,re,glob,sys,base64,urllib.request,websocket
js_expr=base64.b64decode(sys.argv[1]).decode('utf-8')
port=None
for p in glob.glob('/proc/*/cmdline'):
    try:
        with open(p,'rb') as f:
            data=f.read().decode('utf-8',errors='ignore')
        m=re.search(r'--remote-debugging-port=(\d+)',data)
        if m:
            port=int(m.group(1))
            break
    except:
        pass
if not port:
    print(json.dumps({'error':'no_chrome_port'}))
    sys.exit(0)
try:
    pages=json.loads(urllib.request.urlopen('http://127.0.0.1:%d/json/list'%port,timeout=5).read())
except:
    print(json.dumps({'error':'cdp_connect_failed'}))
    sys.exit(0)
ws_url=None
for p in pages:
    if 'crunchyroll' in p.get('url',''):
        ws_url=p.get('webSocketDebuggerUrl')
        break
if not ws_url and pages:
    ws_url=pages[0].get('webSocketDebuggerUrl')
if not ws_url:
    print(json.dumps({'error':'no_pages'}))
    sys.exit(0)
ws=websocket.create_connection(ws_url,suppress_origin=True,timeout=30)
ws.send(json.dumps({'id':1,'method':'Runtime.evaluate','params':{'expression':'window.location.hostname','returnByValue':True}}))
r=json.loads(ws.recv())
h=r.get('result',{}).get('result',{}).get('value','')
if 'crunchyroll' not in h:
    ws.send(json.dumps({'id':2,'method':'Page.navigate','params':{'url':'https://www.crunchyroll.com/'}}))
    ws.recv()
    import time
    time.sleep(5)
ws.send(json.dumps({'id':3,'method':'Runtime.evaluate','params':{'expression':js_expr,'awaitPromise':True,'returnByValue':True}}))
r=json.loads(ws.recv())
v=r.get('result',{}).get('result',{}).get('value','{}')
ws.close()
print(v)
";

    /// <summary>
    /// Executes a JavaScript expression inside Chrome via CDP (Chrome DevTools Protocol).
    /// First tries direct WebSocket connection (if ChromeCdpUrl is configured), then falls
    /// back to running a Python script inside the FlareSolverr Docker container via docker exec.
    /// The JS expression must return a string value (use JSON.stringify for objects).
    /// This is the low-level method used by <see cref="CdpFetchJsonAsync"/> and can also
    /// be called directly for custom JavaScript execution.
    /// </summary>
    /// <param name="dockerContainerName">The Docker container name/ID of FlareSolverr (for docker exec fallback).</param>
    /// <param name="jsExpression">The JavaScript expression to evaluate (must be an async IIFE that returns a string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The string result from the JS expression, or null if failed.</returns>
    public async Task<string?> ExecuteCdpJsAsync(string dockerContainerName, string jsExpression, CancellationToken cancellationToken = default)
    {
        // Ensure Chrome is running inside FlareSolverr before trying CDP.
        // FlareSolverr v3.x kills Chrome after each request unless a session is active.
        if (IsConfigured)
        {
            await EnsureBrowserAliveAsync(cancellationToken).ConfigureAwait(false);
        }

        // Strategy 1: Direct WebSocket CDP connection (no Docker access needed)
        if (!string.IsNullOrWhiteSpace(_chromeCdpUrl))
        {
            _logger.LogDebug("[CDP] Trying direct WebSocket connection to {Url}...", _chromeCdpUrl);
            var directResult = await ExecuteJsViaCdpDirectAsync(jsExpression, cancellationToken).ConfigureAwait(false);
            if (directResult != null)
            {
                return directResult;
            }

            _logger.LogWarning("[CDP] Direct WebSocket failed, falling back to docker exec...");
        }

        // Strategy 2: Docker exec + Python (requires Docker socket access)
        if (string.IsNullOrWhiteSpace(dockerContainerName))
        {
            _logger.LogDebug("[CDP] Docker container name not configured and direct CDP failed");
            return null;
        }

        try
        {
            _logger.LogDebug("[CDP] Executing JS via CDP in container '{Container}'...", dockerContainerName);

            // Base64 encode the Python script and JS expression separately.
            // The Python script is passed as the -c argument, and the JS expression
            // is passed as a positional argument (sys.argv[1]) — both base64-encoded
            // to completely avoid shell escaping issues.
            var scriptBytes = Encoding.UTF8.GetBytes(CdpFetchScript.Trim());
            var scriptB64 = Convert.ToBase64String(scriptBytes);
            var jsB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsExpression));

            var pythonBootstrap = $"import base64; exec(base64.b64decode('{scriptB64}'))";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "exec", dockerContainerName, "python3", "-c", pythonBootstrap, jsB64 },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[CDP] Docker exec timed out after 60s");
                try { process.Kill(); } catch { /* ignore */ }
                return null;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("[CDP] Docker exec failed (exit {ExitCode}). Stderr: {Stderr}",
                    process.ExitCode, stderr.Length > 500 ? stderr[..500] : stderr);
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning("[CDP] Empty output from script. Stderr: {Stderr}", stderr);
                return null;
            }

            var output = stdout.Trim();

            // Check for script-level errors (CDP connection issues, no Chrome found, etc.)
            if (output.StartsWith("{") && output.Contains("\"error\""))
            {
                try
                {
                    var errorObj = JsonSerializer.Deserialize<JsonElement>(output);
                    if (errorObj.TryGetProperty("error", out var errProp))
                    {
                        var errorMsg = errProp.GetString();
                        // Only treat as error if it's a script/CDP error, not an API error
                        if (errorMsg == "no_chrome_port" || errorMsg == "cdp_connect_failed" || errorMsg == "no_pages")
                        {
                            _logger.LogWarning("[CDP] Script error: {Error}", errorMsg);
                            return null;
                        }
                    }
                }
                catch { /* not parseable, continue with raw output */ }
            }

            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[CDP] Error running script in container '{Container}'", dockerContainerName);
            return null;
        }
    }

    /// <summary>
    /// Makes an HTTP request via Chrome's fetch() API using CDP (Chrome DevTools Protocol).
    /// Executes inside FlareSolverr's Chrome browser, bypassing Cloudflare TLS fingerprinting.
    /// Use relative URLs (e.g., "/auth/v1/token") to stay on the crunchyroll.com origin,
    /// or full URLs if Chrome is already on the correct domain.
    /// </summary>
    /// <param name="dockerContainerName">The Docker container name/ID of FlareSolverr.</param>
    /// <param name="url">The URL to fetch (relative like "/api/..." or absolute).</param>
    /// <param name="method">HTTP method (GET, POST, etc.).</param>
    /// <param name="headers">Optional request headers.</param>
    /// <param name="body">Optional request body (for POST/PUT).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The JSON response as a string, or null if failed.</returns>
    public async Task<string?> CdpFetchJsonAsync(
        string dockerContainerName,
        string url,
        string method = "GET",
        Dictionary<string, string>? headers = null,
        string? body = null,
        CancellationToken cancellationToken = default)
    {
        // Build JavaScript fetch() options object
        var optionParts = new List<string>();

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            optionParts.Add($"method:'{EscapeJsString(method)}'");
        }

        if (headers != null && headers.Count > 0)
        {
            var headerEntries = headers.Select(h => $"'{EscapeJsString(h.Key)}':'{EscapeJsString(h.Value)}'");
            optionParts.Add($"headers:{{{string.Join(",", headerEntries)}}}");
        }

        if (!string.IsNullOrEmpty(body))
        {
            optionParts.Add($"body:'{EscapeJsString(body)}'");
        }

        var fetchOptions = optionParts.Count > 0 ? "{" + string.Join(",", optionParts) + "}" : "{}";

        // Build the async IIFE: fetch → parse JSON → return as string
        var jsExpression = $"(async()=>{{try{{const r=await fetch('{EscapeJsString(url)}',{fetchOptions});const d=await r.json();return JSON.stringify(d)}}catch(e){{return JSON.stringify({{error:e.message}})}}}})()";

        _logger.LogDebug("[CDP Fetch] {Method} {Url}", method, url);

        return await ExecuteCdpJsAsync(dockerContainerName, jsExpression, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Navigates Chrome to a Crunchyroll series page and waits for the SPA to fully render,
    /// then extracts episode data directly from the rendered DOM via JavaScript.
    /// Unlike HTML scraping (which gets only placeholder skeletons), this waits for React
    /// to hydrate and the episode cards to appear with real data.
    /// </summary>
    /// <param name="dockerContainerName">The Docker container name for CDP access.</param>
    /// <param name="seriesUrl">The full URL of the series page (e.g., https://www.crunchyroll.com/series/GY8V11X7Y).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON string with extracted episode data, or null if failed.</returns>
    public async Task<string?> CdpExtractRenderedEpisodesAsync(
        string dockerContainerName,
        string seriesUrl,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        // First, navigate to the series page via FlareSolverr (handles Cloudflare challenge)
        _logger.LogInformation("[CDP DOM] Navigating to {Url} and waiting for SPA render...", seriesUrl);
        var html = await GetPageContentAsync(seriesUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html))
        {
            _logger.LogWarning("[CDP DOM] Failed to navigate to series page");
            return null;
        }

        // Now the Chrome tab is on the series page. The initial HTML has placeholders,
        // but React is hydrating in the background. Use CDP to run JavaScript that
        // waits for the real episode cards to appear, then extracts the data.
        var jsExpression = @"(async()=>{
            // Wait for episode cards to render (replace skeleton placeholders)
            // The SPA loads episodes via API and renders them as <a> tags with /watch/ hrefs
            const maxWait = 30000;
            const interval = 500;
            let elapsed = 0;
            
            while (elapsed < maxWait) {
                // Check for real episode cards (not placeholders)
                const cards = document.querySelectorAll('a[href*=""/watch/""]');
                // Need more than 1 (the prologue link might already be in SSR HTML)
                if (cards.length > 2) break;
                
                // Also check for the playable-card elements with actual content
                const playableCards = document.querySelectorAll('.playable-card-static');
                if (playableCards.length > 0) break;
                
                await new Promise(r => setTimeout(r, interval));
                elapsed += interval;
            }
            
            // Extract episode data from the rendered DOM
            const episodes = [];
            const cards = document.querySelectorAll('a[href*=""/watch/""]');
            
            for (const card of cards) {
                const href = card.getAttribute('href') || '';
                const idMatch = href.match(/\/watch\/([A-Z0-9]+)/);
                if (!idMatch) continue;
                
                const ep = { id: idMatch[1] };
                
                // Title from various possible elements
                const titleEl = card.querySelector('h4') || card.querySelector('[data-t=""title""]');
                if (titleEl) ep.title = titleEl.textContent.trim();
                
                // Episode number
                const metaEl = card.querySelector('[data-t=""meta""]') || card.querySelector('.text--is-m--pqiL-');
                if (metaEl) {
                    const metaText = metaEl.textContent.trim();
                    const numMatch = metaText.match(/[EÉ](\d+)/i) || metaText.match(/Episode\s*(\d+)/i);
                    if (numMatch) ep.episodeNumber = numMatch[1];
                    ep.meta = metaText;
                }
                
                // Thumbnail
                const img = card.querySelector('img');
                if (img) ep.thumbnail = img.src || img.getAttribute('data-src');
                
                // Duration
                const durEl = card.querySelector('[data-t=""duration-info""]') || card.querySelector('.badge--2TYHP');
                if (durEl) ep.duration = durEl.textContent.trim();
                
                // Avoid duplicates
                if (!episodes.find(e => e.id === ep.id)) {
                    episodes.push(ep);
                }
            }
            
            // Also grab series info while we're here
            const series = {};
            const ogTitle = document.querySelector('meta[property=""og:title""]');
            if (ogTitle) series.title = ogTitle.content;
            const ogImage = document.querySelector('meta[property=""og:image""]');
            if (ogImage) series.image = ogImage.content;
            const ogDesc = document.querySelector('meta[property=""og:description""]');
            if (ogDesc) series.description = ogDesc.content;
            
            // Season info from h4 with currentseasonid
            const h4 = document.querySelector('h4[currentseasonid]');
            if (h4) {
                series.currentSeasonId = h4.getAttribute('currentseasonid');
                series.seasonTitle = h4.getAttribute('seasontitle');
                series.seriesId = h4.getAttribute('seriesid');
            }
            
            return JSON.stringify({
                episodes: episodes,
                series: series,
                cardCount: cards.length,
                waitedMs: elapsed
            });
        })()";

        var result = await ExecuteCdpJsAsync(dockerContainerName, jsExpression, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result))
        {
            _logger.LogInformation("[CDP DOM] Extracted rendered data from series page");
        }
        else
        {
            _logger.LogWarning("[CDP DOM] Failed to extract data from rendered page");
        }

        return result;
    }

    /// <summary>
    /// Escapes a string for safe use inside JavaScript single-quoted string literals.
    /// </summary>
    private static string EscapeJsString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    /// <summary>
    /// Gets or creates a FlareSolverr session ID for persistent browser state.
    /// </summary>
    private string GetOrCreateSession()
    {
        if (_sessionId != null)
        {
            return _sessionId;
        }

        _sessionId = $"crunchyroll_{Guid.NewGuid():N}".Substring(0, 20);
        _logger.LogDebug("[FlareSolverr] Created session: {SessionId}", _sessionId);
        return _sessionId;
    }

    /// <summary>
    /// Destroys the current FlareSolverr session to free resources.
    /// </summary>
    public async Task DestroySessionAsync(CancellationToken cancellationToken = default)
    {
        // Use a fresh timeout so destroy works even if the caller's token is already cancelled
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cts.Token;

        // Destroy the CDP session (keeps Chrome alive for CDP operations)
        if (_cdpSessionId != null)
        {
            try
            {
                var cdpRequest = new { cmd = "sessions.destroy", session = _cdpSessionId };
                await _httpClient.PostAsJsonAsync($"{_flareSolverrUrl}/v1", cdpRequest, token).ConfigureAwait(false);
                _logger.LogDebug("[FlareSolverr] Destroyed CDP session: {SessionId}", _cdpSessionId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[FlareSolverr] CDP session cleanup skipped (will expire on its own)");
            }
            finally
            {
                _cdpSessionId = null;
                _cdpSessionActive = false;
            }
        }

        // Destroy the regular scraping session
        if (_sessionId == null)
        {
            return;
        }

        try
        {
            var request = new { cmd = "sessions.destroy", session = _sessionId };
            await _httpClient.PostAsJsonAsync($"{_flareSolverrUrl}/v1", request, token).ConfigureAwait(false);
            _logger.LogDebug("[FlareSolverr] Destroyed session: {SessionId}", _sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FlareSolverr] Session cleanup skipped (will expire on its own)");
        }
        finally
        {
            _sessionId = null;
        }
    }

    /// <summary>
    /// Invalidates the cached Cloudflare cookies, forcing a fresh fetch next time.
    /// </summary>
    public static void InvalidateCookieCache()
    {
        _cachedUserAgent = null;
        _cachedCookies = null;
        _cookieCacheExpiration = DateTime.MinValue;
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
            // Try to clean up sessions before disposing HttpClient
            if (_sessionId != null || _cdpSessionId != null)
            {
                try
                {
                    DestroySessionAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore cleanup errors — sessions expire on their own
                }
            }

            _httpClient.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>
/// Result of obtaining Cloudflare bypass cookies from FlareSolverr.
/// </summary>
public class CloudflareCookieResult
{
    /// <summary>
    /// Gets or sets the user-agent string used by FlareSolverr's browser.
    /// Must be used in conjunction with the cookies for cf_clearance to be valid.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cookies obtained from FlareSolverr (including cf_clearance).
    /// </summary>
    public List<FlareSolverrCookie> Cookies { get; set; } = new();

    /// <summary>
    /// Gets the cf_clearance cookie value if present.
    /// </summary>
    public string? CfClearance => Cookies.FirstOrDefault(c => c.Name == "cf_clearance")?.Value;

    /// <summary>
    /// Gets a value indicating whether this result contains valid Cloudflare cookies.
    /// </summary>
    public bool HasCfClearance => CfClearance != null;
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
    public int MaxTimeout { get; set; } = 120000;

    /// <summary>
    /// Gets or sets the time to wait for JavaScript to load (in milliseconds).
    /// </summary>
    [JsonPropertyName("wait")]
    public int Wait { get; set; } = 12000;

    /// <summary>
    /// Gets or sets the session ID for persistent browser state.
    /// Using sessions keeps cookies and browser context across requests.
    /// </summary>
    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Session { get; set; }

    /// <summary>
    /// Gets or sets custom HTTP headers for the request.
    /// These are passed to the browser via FlareSolverr.
    /// </summary>
    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets the POST body data (for request.post command).
    /// </summary>
    [JsonPropertyName("postData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PostData { get; set; }
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

/// <summary>
/// Result from CDP-based authentication via FlareSolverr's Chrome browser.
/// </summary>
public class CdpAuthResult
{
    /// <summary>
    /// Gets or sets the access token for Crunchyroll API.
    /// </summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>
    /// Gets or sets the token type (typically "Bearer").
    /// </summary>
    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    /// <summary>
    /// Gets or sets the token expiration time in seconds.
    /// </summary>
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Gets or sets the country code from the auth response.
    /// </summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>
    /// Gets or sets the error message if authentication failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
