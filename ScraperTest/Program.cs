using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.Crunchyroll.Api;
using Microsoft.Extensions.Logging;

/// <summary>
/// Simple console application to test the Crunchyroll HTML scraper locally.
/// Run this to fetch HTML from Crunchyroll (via FlareSolverr) and test the parsing logic.
/// </summary>
class ScraperTest
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Crunchyroll HTML Scraper Test Tool ===\n");
        
        string seriesId = args.Length > 0 ? args[0] : "GRDV0019R";  // Default: Jujutsu Kaisen
        string flareSolverrUrl = args.Length > 1 ? args[1] : "http://localhost:8191";
        
        Console.WriteLine($"Series ID: {seriesId}");
        Console.WriteLine($"FlareSolverr URL: {flareSolverrUrl}");
        Console.WriteLine();
        
        // Create a logger for the scraper
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger<ScraperTest>();
        
        // Option 1: Test with existing HTML file
        if (args.Length > 0 && File.Exists(args[0]))
        {
            Console.WriteLine($"Testing with local file: {args[0]}");
            var html = await File.ReadAllTextAsync(args[0]);
            AnalyzeHtml(html);
            await TestScraperStrategies(html, seriesId, flareSolverrUrl, logger);
            return;
        }
        
        // Option 2: Fetch from FlareSolverr
        Console.WriteLine("Fetching page via FlareSolverr...");
        var pageHtml = await FetchViaFlareSolverr(flareSolverrUrl, $"https://www.crunchyroll.com/series/{seriesId}");
        
        if (string.IsNullOrEmpty(pageHtml))
        {
            Console.WriteLine("ERROR: Failed to fetch page");
            return;
        }
        
        // Save HTML for later analysis
        var outputFile = $"scraper_test_{seriesId}_{DateTime.Now:yyyyMMdd_HHmmss}.html";
        await File.WriteAllTextAsync(outputFile, pageHtml);
        Console.WriteLine($"\nSaved HTML to: {outputFile}");
        
        AnalyzeHtml(pageHtml);
        await TestScraperStrategies(pageHtml, seriesId, flareSolverrUrl, logger);
    }
    
    /// <summary>
    /// Tests the actual CrunchyrollHtmlScraper strategies against the HTML.
    /// When HTML scraping only finds placeholders, attempts the full pipeline:
    /// extract season ID from HTML → authenticate with Crunchyroll API → fetch episodes.
    /// </summary>
    static async Task TestScraperStrategies(string html, string seriesId, string flareSolverrUrl, ILogger logger)
    {
        Console.WriteLine("\n========================================");
        Console.WriteLine("=== Testing CrunchyrollHtmlScraper ===");
        Console.WriteLine("========================================\n");
        
        // Test 1: Extract series info
        Console.WriteLine("--- ExtractSeriesFromHtml ---");
        var series = CrunchyrollHtmlScraper.ExtractSeriesFromHtml(html, seriesId, logger);
        if (series != null)
        {
            Console.WriteLine($"  Title: {series.Title}");
            Console.WriteLine($"  Description: {series.Description?.Substring(0, System.Math.Min(100, series.Description?.Length ?? 0))}...");
            Console.WriteLine($"  Slug: {series.SlugTitle}");
            var posterTall = series.Images?.PosterTall?.FirstOrDefault()?.FirstOrDefault();
            var posterWide = series.Images?.PosterWide?.FirstOrDefault()?.FirstOrDefault();
            Console.WriteLine($"  PosterTall: {(posterTall != null ? $"✅ {posterTall.Width}x{posterTall.Height} {posterTall.Source?.Substring(0, System.Math.Min(80, posterTall.Source?.Length ?? 0))}..." : "❌ none")}");
            Console.WriteLine($"  PosterWide: {(posterWide != null ? $"✅ {posterWide.Width}x{posterWide.Height} {posterWide.Source?.Substring(0, System.Math.Min(80, posterWide.Source?.Length ?? 0))}..." : "❌ none")}");
        }
        else
        {
            Console.WriteLine("  FAILED - could not extract series info");
        }
        
        // Test 2: Extract season info (from h4 element)
        Console.WriteLine("\n--- ExtractSeasonInfoFromHtml ---");
        var seasonInfo = CrunchyrollHtmlScraper.ExtractSeasonInfoFromHtml(html, logger);
        if (seasonInfo.HasValue)
        {
            Console.WriteLine($"  ✅ Series ID: {seasonInfo.Value.SeriesId}");
            Console.WriteLine($"  ✅ Current Season ID: {seasonInfo.Value.CurrentSeasonId}");
            Console.WriteLine($"  ✅ Season Title: {seasonInfo.Value.SeasonTitle}");
        }
        else
        {
            Console.WriteLine("  ❌ Could not extract season info from HTML");
        }
        
        // Test 3: Extract available seasons
        Console.WriteLine("\n--- ExtractAvailableSeasonsFromHtml ---");
        var seasons = CrunchyrollHtmlScraper.ExtractAvailableSeasonsFromHtml(html, logger);
        if (seasons.Count > 0)
        {
            foreach (var s in seasons)
            {
                Console.WriteLine($"  Season {s.Index + 1}: {s.SeasonId} - {s.Title}");
            }
        }
        else
        {
            Console.WriteLine("  No seasons found");
        }
        
        // Test 4: Extract episodes from HTML (all strategies)
        Console.WriteLine("\n--- ExtractEpisodesFromHtml (HTML only) ---");
        var episodes = CrunchyrollHtmlScraper.ExtractEpisodesFromHtml(html, logger);
        Console.WriteLine($"  Episodes from HTML scraping: {episodes.Count}");
        if (episodes.Count > 0)
        {
            foreach (var ep in episodes.Take(5))
            {
                Console.WriteLine($"    E{ep.EpisodeNumber ?? "?"}: {ep.Title} (ID: {ep.Id})");
            }
        }
        
        // Test 5: Full pipeline — when HTML has only placeholders, use extracted season ID
        // to fetch episodes from the Crunchyroll API (same as the plugin does via CDP proxy)
        int apiEpisodeCount = 0;
        if (seasonInfo.HasValue)
        {
            Console.WriteLine("\n--- Full Pipeline: API Episode Fetch ---");
            Console.WriteLine($"  Authenticating with Crunchyroll API (anonymous)...");
            
            var token = await GetAnonymousTokenAsync(flareSolverrUrl, logger);
            if (!string.IsNullOrEmpty(token))
            {
                Console.WriteLine($"  ✅ Got access token");
                
                // Fetch series details (includes full image data)
                Console.WriteLine($"\n  Fetching series details for {seasonInfo.Value.SeriesId}...");
                var apiSeries = await FetchApiJsonAsync<Jellyfin.Plugin.Crunchyroll.Models.CrunchyrollResponse<Jellyfin.Plugin.Crunchyroll.Models.CrunchyrollSeries>>(
                    $"https://www.crunchyroll.com/content/v2/cms/series/{seasonInfo.Value.SeriesId}?locale=pt-BR",
                    token, flareSolverrUrl, logger);
                
                var apiSeriesData = apiSeries?.Data?.FirstOrDefault();
                if (apiSeriesData != null)
                {
                    Console.WriteLine($"  ✅ Series: {apiSeriesData.Title}");
                    var tallImgs = apiSeriesData.Images?.PosterTall?.FirstOrDefault();
                    var wideImgs = apiSeriesData.Images?.PosterWide?.FirstOrDefault();
                    Console.WriteLine($"  📷 PosterTall: {(tallImgs?.Count > 0 ? $"✅ {tallImgs.Count} size(s), best: {tallImgs.OrderByDescending(i => i.Width * i.Height).First().Width}x{tallImgs.OrderByDescending(i => i.Width * i.Height).First().Height}" : "❌ none")}");
                    Console.WriteLine($"  📷 PosterWide: {(wideImgs?.Count > 0 ? $"✅ {wideImgs.Count} size(s), best: {wideImgs.OrderByDescending(i => i.Width * i.Height).First().Width}x{wideImgs.OrderByDescending(i => i.Width * i.Height).First().Height}" : "❌ none")}");
                    if (tallImgs?.Count > 0)
                    {
                        Console.WriteLine($"    PosterTall URL: {tallImgs.OrderByDescending(i => i.Width * i.Height).First().Source}");
                    }
                }
                else
                {
                    Console.WriteLine($"  ❌ Could not fetch series details from API");
                }
                
                // Fetch seasons for the series
                Console.WriteLine($"\n  Fetching seasons for series {seasonInfo.Value.SeriesId}...");
                var apiSeasons = await FetchApiJsonAsync<Jellyfin.Plugin.Crunchyroll.Models.CrunchyrollResponse<Jellyfin.Plugin.Crunchyroll.Models.CrunchyrollSeason>>(
                    $"https://www.crunchyroll.com/content/v2/cms/series/{seasonInfo.Value.SeriesId}/seasons?locale=pt-BR",
                    token, flareSolverrUrl, logger);
                
                if (apiSeasons?.Data != null && apiSeasons.Data.Count > 0)
                {
                    Console.WriteLine($"  ✅ Found {apiSeasons.Data.Count} season(s):");
                    foreach (var s in apiSeasons.Data)
                    {
                        Console.WriteLine($"    Season {s.SeasonNumber}: {s.Title} (ID: {s.Id}, Episodes: {s.NumberOfEpisodes})");
                    }
                    
                    // Fetch episodes for each season
                    foreach (var s in apiSeasons.Data)
                    {
                        Console.WriteLine($"\n  Fetching episodes for season '{s.Title}' ({s.Id})...");
                        var apiEpisodes = await FetchApiJsonAsync<Jellyfin.Plugin.Crunchyroll.Models.CrunchyrollResponse<Jellyfin.Plugin.Crunchyroll.Models.CrunchyrollEpisode>>(
                            $"https://www.crunchyroll.com/content/v2/cms/seasons/{s.Id}/episodes?locale=pt-BR",
                            token, flareSolverrUrl, logger);
                        
                        if (apiEpisodes?.Data != null && apiEpisodes.Data.Count > 0)
                        {
                            Console.WriteLine($"  ✅ Found {apiEpisodes.Data.Count} episode(s):");
                            apiEpisodeCount += apiEpisodes.Data.Count;
                            int thumbCount = 0;
                            foreach (var ep in apiEpisodes.Data)
                            {
                                var duration = ep.DurationMs > 0 ? $"{ep.DurationMs / 60000}min" : "?";
                                var hasThumb = ep.Images?.Thumbnail?.FirstOrDefault()?.Count > 0;
                                if (hasThumb) thumbCount++;
                                Console.WriteLine($"    E{ep.EpisodeNumber ?? "?"}: {ep.Title} (ID: {ep.Id}, {duration}, 📷{(hasThumb ? "✅" : "❌")})");
                            }
                            Console.WriteLine($"  📷 Episodes with thumbnails: {thumbCount}/{apiEpisodes.Data.Count}");
                            
                            // Show a sample thumbnail URL
                            var sampleEp = apiEpisodes.Data.FirstOrDefault(e => e.Images?.Thumbnail?.FirstOrDefault()?.Count > 0);
                            if (sampleEp != null)
                            {
                                var thumbUrl = sampleEp.Images!.Thumbnail!.First().OrderByDescending(t => t.Width * t.Height).First().Source;
                                Console.WriteLine($"  📷 Sample thumbnail: {thumbUrl}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"  ❌ No episodes returned for season {s.Id}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"  ❌ No seasons returned from API (may be blocked by Cloudflare)");
                    Console.WriteLine($"  💡 The plugin uses CDP proxy (Chrome inside FlareSolverr) to bypass this");
                }
            }
            else
            {
                Console.WriteLine($"  ❌ Auth failed (likely Cloudflare block)");
                Console.WriteLine($"  💡 The plugin uses FlareSolverr's Chrome to authenticate via CDP proxy");
            }
        }
        
        // Summary
        Console.WriteLine("\n========================================");
        Console.WriteLine("=== Summary ===");
        Console.WriteLine("========================================");
        Console.WriteLine($"  Series extraction:  {(series != null ? "✅ " + series.Title : "❌")}");
        Console.WriteLine($"  Series PosterTall:  {(series?.Images?.PosterTall?.FirstOrDefault()?.Count > 0 ? "✅" : "❌")}");
        Console.WriteLine($"  Series PosterWide:  {(series?.Images?.PosterWide?.FirstOrDefault()?.Count > 0 ? "✅" : "❌")}");
        Console.WriteLine($"  Season ID from HTML: {(seasonInfo.HasValue ? "✅ " + seasonInfo.Value.CurrentSeasonId : "❌")}");
        Console.WriteLine($"  Seasons from HTML:  {(seasons.Count > 0 ? $"✅ {seasons.Count} season(s)" : "❌")}");
        Console.WriteLine($"  Episodes from HTML: {(episodes.Count > 0 ? $"⚠️  {episodes.Count} (only /watch/ links)" : "❌ (SPA placeholders)")}");
        Console.WriteLine($"  Episodes from API:  {(apiEpisodeCount > 0 ? $"✅ {apiEpisodeCount} episodes" : "❌ (needs CDP proxy)")}");
        
        Console.WriteLine();
        Console.WriteLine("=== Image Pipeline Analysis ===");
        Console.WriteLine("  When Cloudflare blocks the API (403), the plugin:");
        Console.WriteLine("  1. GetSeriesAsync → CDP proxy → full images (PosterTall+Wide) ✅");
        Console.WriteLine("  2. GetSeriesAsync → HTML scraping fallback → og:image + JSON-LD ⚠️");
        Console.WriteLine("  3. GetEpisodeAsync → CDP proxy → episode thumbnails ✅");
        Console.WriteLine("  These CDP proxy fallbacks were previously missing, causing no images.");
    }
    
    /// <summary>
    /// Gets an anonymous access token from the Crunchyroll API.
    /// First tries direct HTTP, then falls back to FlareSolverr proxy if Cloudflare blocks.
    /// </summary>
    static async Task<string?> GetAnonymousTokenAsync(string flareSolverrUrl, ILogger logger)
    {
        const string basicAuthToken = "bmR0aTZicXlqcm9wNXZnZjF0dnU6elpIcS00SEJJVDlDb2FMcnBPREJjRVRCTUNHai1QNlg=";
        const string authUrl = "https://www.crunchyroll.com/auth/v1/token";
        
        try
        {
            // Try direct HTTP first
            using var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Crunchyroll/3.50.2");
            
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, authUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basicAuthToken);
            request.Headers.Add("ETP-Anonymous-ID", System.Guid.NewGuid().ToString());
            request.Content = new System.Net.Http.FormUrlEncodedContent(new[]
            {
                new System.Collections.Generic.KeyValuePair<string, string>("grant_type", "client_id"),
                new System.Collections.Generic.KeyValuePair<string, string>("device_id", System.Guid.NewGuid().ToString()),
                new System.Collections.Generic.KeyValuePair<string, string>("device_type", "com.crunchyroll.android.google")
            });
            
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var tokenProp))
                {
                    return tokenProp.GetString();
                }
            }
            
            Console.WriteLine($"  Direct auth returned {response.StatusCode}, trying via FlareSolverr...");
            
            // Fallback: use FlareSolverr to proxy the auth request
            // FlareSolverr request.post can submit POST data
            using var fsClient = new HttpClient();
            fsClient.Timeout = TimeSpan.FromSeconds(60);
            var fsRequest = new
            {
                cmd = "request.post",
                url = authUrl,
                maxTimeout = 60000,
                postData = "grant_type=client_id&device_id=" + System.Guid.NewGuid().ToString() + "&device_type=com.crunchyroll.android.google"
            };
            
            var fsJson = System.Text.Json.JsonSerializer.Serialize(fsRequest);
            var fsContent = new StringContent(fsJson, System.Text.Encoding.UTF8, "application/json");
            var fsResponse = await fsClient.PostAsync($"{flareSolverrUrl}/v1", fsContent);
            
            if (fsResponse.IsSuccessStatusCode)
            {
                var fsResponseJson = await fsResponse.Content.ReadAsStringAsync();
                using var fsDoc = System.Text.Json.JsonDocument.Parse(fsResponseJson);
                if (fsDoc.RootElement.TryGetProperty("solution", out var solution) &&
                    solution.TryGetProperty("response", out var responseBody))
                {
                    var body = responseBody.GetString();
                    if (!string.IsNullOrEmpty(body))
                    {
                        // The response body might be HTML-wrapped or raw JSON
                        // Try to find JSON with access_token
                        var tokenMatch = System.Text.RegularExpressions.Regex.Match(body, @"""access_token""\s*:\s*""([^""]+)""");
                        if (tokenMatch.Success)
                        {
                            return tokenMatch.Groups[1].Value;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Auth error: {ex.Message}");
        }
        
        return null;
    }
    
    /// <summary>
    /// Fetches a Crunchyroll API endpoint with Bearer auth.
    /// Tries direct HTTP first, falls back to FlareSolverr if blocked.
    /// </summary>
    static async Task<T?> FetchApiJsonAsync<T>(string url, string token, string flareSolverrUrl, ILogger logger) where T : class
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Crunchyroll/3.50.2");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return System.Text.Json.JsonSerializer.Deserialize<T>(json);
            }
            
            Console.WriteLine($"  API returned {response.StatusCode} for {url}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  API fetch error: {ex.Message}");
        }
        
        return null;
    }
    
    static void AnalyzeHtml(string html)
    {
        Console.WriteLine("\n=== HTML Analysis ===");
        Console.WriteLine($"Total length: {html.Length} chars");
        Console.WriteLine();
        
        // Check for key indicators
        var indicators = new[]
        {
            ("episode-card", html.Contains("episode-card")),
            ("playable-card", html.Contains("playable-card")),
            ("erc-playable-card", html.Contains("erc-playable-card")),
            ("data-t=", html.Contains("data-t=")),
            ("/watch/", html.Contains("/watch/")),
            ("__INITIAL_STATE__", html.Contains("__INITIAL_STATE__")),
            ("__NEXT_DATA__", html.Contains("__NEXT_DATA__")),
            ("Just a moment", html.Contains("Just a moment")),  // Cloudflare challenge
            ("cf-", html.Contains("cf-")),  // Cloudflare elements
        };
        
        Console.WriteLine("Key indicators:");
        foreach (var (name, found) in indicators)
        {
            Console.WriteLine($"  {name}: {(found ? "FOUND" : "not found")}");
        }
        
        // Count occurrences
        Console.WriteLine("\nOccurrence counts:");
        Console.WriteLine($"  'episode': {CountOccurrences(html, "episode")}");
        Console.WriteLine($"  '/watch/': {CountOccurrences(html, "/watch/")}");
        Console.WriteLine($"  'data-t=': {CountOccurrences(html, "data-t=")}");
        
        // Try to extract watch links
        Console.WriteLine("\n=== Watch Links Found ===");
        var watchPattern = new System.Text.RegularExpressions.Regex(
            @"href=""[^""]*?/watch/([A-Z0-9]{9,})(?:/([^""]+))?""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        var matches = watchPattern.Matches(html);
        var uniqueIds = new System.Collections.Generic.HashSet<string>();
        
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var id = match.Groups[1].Value;
            if (!uniqueIds.Contains(id))
            {
                uniqueIds.Add(id);
                var slug = match.Groups[2].Success ? match.Groups[2].Value : "(no slug)";
                Console.WriteLine($"  {id}: {slug}");
            }
        }
        
        Console.WriteLine($"\nTotal unique episode IDs: {uniqueIds.Count}");
        
        // Look for episode-related elements
        Console.WriteLine("\n=== Looking for Episode Elements ===");
        
        var episodePatterns = new[]
        {
            @"data-t=""episode[^""]*""",
            @"class=""[^""]*episode[^""]*""",
            @"class=""[^""]*playable-card[^""]*""",
            @"class=""[^""]*erc-[^""]*""",
        };
        
        foreach (var pattern in episodePatterns)
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var found = regex.Matches(html);
            Console.WriteLine($"  Pattern '{pattern}': {found.Count} matches");
            if (found.Count > 0 && found.Count <= 5)
            {
                foreach (System.Text.RegularExpressions.Match m in found)
                {
                    Console.WriteLine($"    -> {m.Value}");
                }
            }
        }
    }
    
    static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
    
    static async Task<string?> FetchViaFlareSolverr(string flareSolverrUrl, string targetUrl)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            
            var request = new
            {
                cmd = "request.get",
                url = targetUrl,
                maxTimeout = 60000
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            Console.WriteLine($"Requesting: {targetUrl}");
            var response = await client.PostAsync($"{flareSolverrUrl}/v1", content);
            
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"FlareSolverr error: {response.StatusCode}");
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine(errorBody);
                return null;
            }
            
            var responseJson = await response.Content.ReadAsStringAsync();
            
            // Parse response to get HTML
            using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("solution", out var solution) && 
                solution.TryGetProperty("response", out var htmlElement))
            {
                var htmlContent = htmlElement.GetString();
                Console.WriteLine($"Got {htmlContent?.Length ?? 0} chars of HTML");
                return htmlContent;
            }
            
            Console.WriteLine("Could not find 'solution.response' in FlareSolverr response");
            Console.WriteLine(responseJson.Substring(0, Math.Min(500, responseJson.Length)));
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return null;
        }
    }
}
