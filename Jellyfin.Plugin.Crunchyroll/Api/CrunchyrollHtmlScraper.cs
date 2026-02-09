using System.Text.RegularExpressions;
using System.Web;
using Jellyfin.Plugin.Crunchyroll.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Api;

/// <summary>
/// Scrapes Crunchyroll HTML pages to extract metadata.
/// Used when API access is blocked by Cloudflare.
/// </summary>
public static partial class CrunchyrollHtmlScraper
{
    /// <summary>
    /// Extracts series information from a Crunchyroll series page HTML.
    /// </summary>
    /// <param name="html">The HTML content of the series page.</param>
    /// <param name="seriesId">The Crunchyroll series ID.</param>
    /// <param name="logger">Logger for debugging.</param>
    /// <returns>The extracted series information or null if parsing failed.</returns>
    public static CrunchyrollSeries? ExtractSeriesFromHtml(string html, string seriesId, ILogger logger)
    {
        try
        {
            var series = new CrunchyrollSeries
            {
                Id = seriesId
            };

            // Extract title from h1
            var titleMatch = TitleRegex().Match(html);
            if (titleMatch.Success)
            {
                series.Title = HttpUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }
            else
            {
                // Try og:title meta tag
                var ogTitleMatch = OgTitleRegex().Match(html);
                if (ogTitleMatch.Success)
                {
                    series.Title = HttpUtility.HtmlDecode(ogTitleMatch.Groups[1].Value.Trim());
                    // Remove " - Watch on Crunchyroll" suffix if present
                    series.Title = series.Title.Replace(" - Watch on Crunchyroll", "")
                                              .Replace(" - Crunchyroll", "");
                }
            }

            // Extract description
            var descMatch = DescriptionRegex().Match(html);
            if (descMatch.Success)
            {
                series.Description = HttpUtility.HtmlDecode(descMatch.Groups[1].Value.Trim());
            }
            else
            {
                // Try og:description meta tag
                var ogDescMatch = OgDescriptionRegex().Match(html);
                if (ogDescMatch.Success)
                {
                    series.Description = HttpUtility.HtmlDecode(ogDescMatch.Groups[1].Value.Trim());
                }
            }

            // Extract poster image from og:image and try to build both tall and wide variants
            var posterMatch = PosterImageRegex().Match(html);
            if (posterMatch.Success)
            {
                var imageUrl = posterMatch.Groups[1].Value;
                series.Images = new CrunchyrollImages
                {
                    PosterTall = new List<List<CrunchyrollImage>>
                    {
                        new List<CrunchyrollImage>
                        {
                            new CrunchyrollImage
                            {
                                Source = imageUrl,
                                Width = 480,
                                Height = 720,
                                Type = "poster_tall"
                            }
                        }
                    }
                };

                // Try to derive PosterWide from the same CDN URL by modifying dimensions
                // Crunchyroll CDN URLs often look like: .../full/...{hash}_full.jpg
                // The og:image is typically the tall poster. Try to also use it for wide/backdrop.
                series.Images.PosterWide = new List<List<CrunchyrollImage>>
                {
                    new List<CrunchyrollImage>
                    {
                        new CrunchyrollImage
                        {
                            Source = imageUrl,
                            Width = 1920,
                            Height = 1080,
                            Type = "poster_wide"
                        }
                    }
                };
            }

            // Try JSON-LD schema for additional/better image data
            var jsonLdMatch = Regex.Match(html, @"<script[^>]*type=""application/ld\+json""[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (jsonLdMatch.Success)
            {
                try
                {
                    var jsonLd = jsonLdMatch.Groups[1].Value.Trim();
                    var imageMatch = Regex.Match(jsonLd, @"""image""\s*:\s*""([^""]+)""");
                    if (imageMatch.Success)
                    {
                        var schemaImageUrl = imageMatch.Groups[1].Value;
                        // JSON-LD image is usually a wide landscape image
                        if (series.Images == null)
                        {
                            series.Images = new CrunchyrollImages();
                        }
                        // Use the schema image as a better wide poster if we have it
                        series.Images.PosterWide = new List<List<CrunchyrollImage>>
                        {
                            new List<CrunchyrollImage>
                            {
                                new CrunchyrollImage
                                {
                                    Source = schemaImageUrl,
                                    Width = 1920,
                                    Height = 1080,
                                    Type = "poster_wide"
                                }
                            }
                        };
                        // Also set PosterTall if we don't have one
                        if (series.Images.PosterTall == null)
                        {
                            series.Images.PosterTall = new List<List<CrunchyrollImage>>
                            {
                                new List<CrunchyrollImage>
                                {
                                    new CrunchyrollImage
                                    {
                                        Source = schemaImageUrl,
                                        Width = 480,
                                        Height = 720,
                                        Type = "poster_tall"
                                    }
                                }
                            };
                        }
                        logger.LogDebug("Extracted JSON-LD image: {Url}", schemaImageUrl);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to extract JSON-LD image data");
                }
            }

            // Extract slug title from URL if available
            var slugMatch = SlugTitleRegex().Match(html);
            if (slugMatch.Success)
            {
                series.SlugTitle = slugMatch.Groups[1].Value;
            }

            if (string.IsNullOrEmpty(series.Title))
            {
                logger.LogWarning("Failed to extract series title from HTML");
                return null;
            }

            logger.LogDebug("Extracted series from HTML: {Title}", series.Title);
            return series;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting series from HTML");
            return null;
        }
    }

    /// <summary>
    /// Extracts available season IDs and titles from the series page HTML.
    /// Crunchyroll pages often have a season selector that contains links to each season.
    /// </summary>
    /// <param name="html">The HTML content of the series page.</param>
    /// <param name="logger">Logger for debugging.</param>
    /// <returns>List of tuples (SeasonId, SeasonTitle, Index) for each season found.</returns>
    public static List<(string SeasonId, string Title, int Index)> ExtractAvailableSeasonsFromHtml(string html, ILogger logger)
    {
        var seasons = new List<(string SeasonId, string Title, int Index)>();
        
        try
        {
            // Look for season selector in the HTML
            // Crunchyroll typically has a dropdown or tabs with season info
            // Pattern 1: Season selector dropdown options
            var seasonMatches = Regex.Matches(html, @"season[_-]?id["":\s]+[""']?([A-Z0-9]{9,})[""']?", RegexOptions.IgnoreCase);
            
            foreach (Match match in seasonMatches)
            {
                var seasonId = match.Groups[1].Value;
                if (!seasons.Any(s => s.SeasonId == seasonId))
                {
                    seasons.Add((seasonId, $"Season {seasons.Count + 1}", seasons.Count));
                }
            }
            
            // Pattern 2: Look for season navigation links
            var seasonLinkMatches = Regex.Matches(html, @"href=""[^""]*?/series/[A-Z0-9]+\?season=([A-Z0-9]{9,})""", RegexOptions.IgnoreCase);
            
            foreach (Match match in seasonLinkMatches)
            {
                var seasonId = match.Groups[1].Value;
                if (!seasons.Any(s => s.SeasonId == seasonId))
                {
                    seasons.Add((seasonId, $"Season {seasons.Count + 1}", seasons.Count));
                }
            }
            
            // Pattern 3: Look in __INITIAL_STATE__ or __NEXT_DATA__ for season info
            var jsonMatch = Regex.Match(html, @"(?:__INITIAL_STATE__|__NEXT_DATA__)\s*=\s*({.+?})(?:;\s*</script>|$)", RegexOptions.Singleline);
            if (jsonMatch.Success)
            {
                var json = jsonMatch.Groups[1].Value;
                
                // Find season objects with IDs
                var seasonDataMatches = Regex.Matches(json, @"""([A-Z0-9]{9,})"":\s*\{[^}]*""type""\s*:\s*""season""[^}]*\}", RegexOptions.IgnoreCase);
                
                foreach (Match sdMatch in seasonDataMatches)
                {
                    var seasonId = sdMatch.Groups[1].Value;
                    if (!seasons.Any(s => s.SeasonId == seasonId))
                    {
                        // Try to extract season title/number from the block
                        var block = sdMatch.Value;
                        var titleMatch = Regex.Match(block, @"""title""\s*:\s*""([^""]+)""");
                        var seasonTitle = titleMatch.Success ? titleMatch.Groups[1].Value : $"Season {seasons.Count + 1}";
                        
                        seasons.Add((seasonId, seasonTitle, seasons.Count));
                    }
                }
            }
            
            // Pattern 4: Extract from <h4> element with seriesid/currentseasonid attributes
            // (Modern CR SPA embeds this in the season navigation section)
            if (seasons.Count == 0)
            {
                var seasonInfo = ExtractSeasonInfoFromHtml(html, logger);
                if (seasonInfo.HasValue)
                {
                    seasons.Add((seasonInfo.Value.CurrentSeasonId, seasonInfo.Value.SeasonTitle, 0));
                }
            }

            logger.LogInformation("[SeasonExtract] Found {Count} seasons in HTML", seasons.Count);
            foreach (var season in seasons)
            {
                logger.LogDebug("[SeasonExtract] Season {Index}: {Id} - {Title}", season.Index + 1, season.SeasonId, season.Title);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SeasonExtract] Failed to extract seasons from HTML");
        }
        
        return seasons;
    }

    /// <summary>
    /// Extracts episode information from a Crunchyroll series page HTML.
    /// </summary>
    /// <param name="html">The HTML content of the series page.</param>
    /// <param name="logger">Logger for debugging.</param>
    /// <param name="debugOutputPath">Optional path to save debug HTML output.</param>
    /// <returns>List of extracted episodes.</returns>
    public static List<CrunchyrollEpisode> ExtractEpisodesFromHtml(string html, ILogger logger, string? debugOutputPath = null)
    {
        var episodes = new List<CrunchyrollEpisode>();

        // Save HTML for debugging if path is provided
        if (!string.IsNullOrEmpty(debugOutputPath))
        {
            try
            {
                var debugFile = Path.Combine(debugOutputPath, $"crunchyroll_debug_{DateTime.Now:yyyyMMdd_HHmmss}.html");
                File.WriteAllText(debugFile, html);
                logger.LogInformation("[HTML Debug] Saved HTML to: {Path}", debugFile);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[HTML Debug] Failed to save debug HTML");
            }
        }

        // Debug: Log HTML structure indicators
        logger.LogInformation("[HTML Scraper] HTML length: {Length} chars", html.Length);
        logger.LogInformation("[HTML Scraper] Contains 'episode-card': {Found}", html.Contains("episode-card"));
        logger.LogInformation("[HTML Scraper] Contains 'playable-card': {Found}", html.Contains("playable-card"));
        logger.LogInformation("[HTML Scraper] Contains 'data-t=': {Found}", html.Contains("data-t="));
        logger.LogInformation("[HTML Scraper] Contains '/watch/': {Found}", html.Contains("/watch/"));
        logger.LogInformation("[HTML Scraper] Contains '__INITIAL_STATE__': {Found}", html.Contains("__INITIAL_STATE__"));
        logger.LogInformation("[HTML Scraper] Contains '__NEXT_DATA__': {Found}", html.Contains("__NEXT_DATA__"));
        logger.LogInformation("[HTML Scraper] Contains 'erc-playable-card': {Found}", html.Contains("erc-playable-card"));
        logger.LogInformation("[HTML Scraper] Contains 'episode': {Count} occurrences", 
            System.Text.RegularExpressions.Regex.Matches(html, "episode", RegexOptions.IgnoreCase).Count);

        try
        {
            // Try multiple extraction strategies
            
            // Strategy 1: Original data-t="episode-card" pattern
            var episodeMatches = EpisodeCardRegex().Matches(html);
            if (episodeMatches.Count > 0)
            {
                logger.LogInformation("[HTML Scraper] Strategy 1: Found {Count} episode cards with data-t='episode-card'", episodeMatches.Count);
                episodes = ExtractFromEpisodeCardMatches(episodeMatches, logger);
            }
            
            // Strategy 2: Try erc-playable-card pattern (newer Crunchyroll structure)
            if (episodes.Count == 0)
            {
                logger.LogInformation("[HTML Scraper] Strategy 2: Trying erc-playable-card pattern...");
                var ercMatches = ErcPlayableCardRegex().Matches(html);
                if (ercMatches.Count > 0)
                {
                    logger.LogInformation("[HTML Scraper] Found {Count} erc-playable-card elements", ercMatches.Count);
                    episodes = ExtractFromErcPlayableCards(ercMatches, logger);
                }
            }
            
            // Strategy 3: Try extracting from __NEXT_DATA__ JSON
            if (episodes.Count == 0 && html.Contains("__NEXT_DATA__"))
            {
                logger.LogInformation("[HTML Scraper] Strategy 3: Trying __NEXT_DATA__ JSON extraction...");
                episodes = ExtractFromNextDataJson(html, logger);
            }
            
            // Strategy 4: Try extracting from __INITIAL_STATE__ JSON
            if (episodes.Count == 0 && html.Contains("__INITIAL_STATE__"))
            {
                logger.LogInformation("[HTML Scraper] Strategy 4: Trying __INITIAL_STATE__ JSON extraction...");
                episodes = ExtractFromInitialStateJson(html, logger);
            }
            
            // Strategy 5: Generic /watch/ link extraction as fallback
            if (episodes.Count == 0)
            {
                logger.LogInformation("[HTML Scraper] Strategy 5: Trying generic /watch/ link extraction...");
                episodes = ExtractFromWatchLinks(html, logger);
            }

            // Strategy 6: If we still have no episodes but found placeholder cards,
            // the page is a modern SPA that loads episodes via client-side JS.
            // Log a hint so the caller (CrunchyrollApiClient) can use the season ID
            // extracted from the HTML to call the API directly.
            if (episodes.Count == 0 && html.Contains("playable-card-placeholder"))
            {
                var seasonInfo = ExtractSeasonInfoFromHtml(html, logger);
                if (seasonInfo.HasValue)
                {
                    logger.LogInformation(
                        "[HTML Scraper] Strategy 6: Page has {PlaceholderCount} loading placeholders (SPA). " +
                        "Extracted currentSeasonId={SeasonId} from HTML. Episodes must be fetched via API.",
                        System.Text.RegularExpressions.Regex.Matches(html, "playable-card-placeholder").Count,
                        seasonInfo.Value.CurrentSeasonId);
                }
                else
                {
                    logger.LogWarning("[HTML Scraper] Page has loading placeholders but could not extract season info from HTML.");
                }
            }

            // Log results
            if (episodes.Count > 0)
            {
                var episodeNumbers = string.Join(", ", episodes.Select(e => $"E{e.EpisodeNumber ?? "?"}"));
                var firstEp = episodes.First();
                var lastEp = episodes.Last();
                logger.LogInformation("[HTML Scraper] Extracted {Count} episodes: {EpisodeList}", episodes.Count, episodeNumbers);
                logger.LogInformation("[HTML Scraper] Episode range: E{First} to E{Last}", firstEp.EpisodeNumber ?? "?", lastEp.EpisodeNumber ?? "?");
            }
            else
            {
                logger.LogWarning("[HTML Scraper] No episodes found after trying all strategies. The page structure may have changed significantly.");
                
                // Log a sample of the HTML for debugging
                var sampleLength = Math.Min(2000, html.Length);
                var sample = html.Substring(0, sampleLength);
                logger.LogWarning("[HTML Scraper] First {Length} chars of HTML: {Sample}", sampleLength, sample);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting episodes from HTML");
        }

        return episodes;
    }

    /// <summary>
    /// Extracts episodes from the original episode-card regex matches.
    /// </summary>
    private static List<CrunchyrollEpisode> ExtractFromEpisodeCardMatches(MatchCollection matches, ILogger logger)
    {
        var episodes = new List<CrunchyrollEpisode>();
        
        foreach (Match match in matches)
        {
            var cardHtml = match.Value;
            var episode = new CrunchyrollEpisode();
            
            // Extract from title link
            var linkMatch = Regex.Match(cardHtml, @"playable-card__title-link""[^>]+href=""(?<url>[^""]+)""[^>]*>(?<fullTitle>[^<]+)</a>", RegexOptions.IgnoreCase);
            
            if (!linkMatch.Success) 
            {
                logger.LogWarning("[HTML Scraper] Could not find title link in card");
                continue;
            }

            string fullTitle = HttpUtility.HtmlDecode(linkMatch.Groups["fullTitle"].Value.Trim());
            string episodeUrl = linkMatch.Groups["url"].Value;

            // Parse metadata from title
            // Supports both "T" (Portuguese/Spanish) and "S" (French/English/German) season prefixes
            var metaMatch = Regex.Match(fullTitle, @"^(?:[TS](?<season>\d+)\s+)?(?:E(?<episode>\d+)\s*[-:]\s*)?(?<title>.+)$", RegexOptions.IgnoreCase);

            if (metaMatch.Success)
            {
                episode.Title = metaMatch.Groups["title"].Value.Trim();
                if (metaMatch.Groups["episode"].Success)
                {
                    episode.EpisodeNumber = metaMatch.Groups["episode"].Value;
                }
            }
            else
            {
                episode.Title = fullTitle;
            }

            // Extract episode number from aria-label if missing
            if (string.IsNullOrEmpty(episode.EpisodeNumber))
            {
                var ariaMatch = Regex.Match(cardHtml, @"aria-label=""Reproduzir[^""]*?Epis[oó]dio\s+(?<num>\d+)", RegexOptions.IgnoreCase);
                if (ariaMatch.Success)
                {
                    episode.EpisodeNumber = ariaMatch.Groups["num"].Value;
                }
            }

            // Set episode number int
            if (int.TryParse(episode.EpisodeNumber, out int epNum))
            {
                episode.EpisodeNumberInt = epNum;
                episode.SequenceNumber = epNum;
            }

            // Extract ID from URL
            if (string.IsNullOrEmpty(episode.Id))
            {
                var urlParts = episodeUrl.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (urlParts.Length >= 2 && urlParts.Contains("watch"))
                {
                    int watchIndex = Array.IndexOf(urlParts, "watch");
                    if (watchIndex + 1 < urlParts.Length)
                    {
                        episode.Id = urlParts[watchIndex + 1];
                    }
                }
            }

            // Extract description
            var descMatch = EpisodeDescriptionRegex().Match(cardHtml);
            if (descMatch.Success)
            {
                episode.Description = HttpUtility.HtmlDecode(descMatch.Groups[1].Value.Trim());
            }

            // Extract thumbnail
            var thumbMatch = EpisodeThumbnailRegex().Match(cardHtml);
            if (thumbMatch.Success)
            {
                var thumbnailUrl = thumbMatch.Groups[1].Value;
                if (thumbnailUrl.Contains("width=") && thumbnailUrl.Contains("height="))
                {
                    thumbnailUrl = thumbnailUrl
                        .Replace("width=320", "width=1920")
                        .Replace("height=180", "height=1080")
                        .Replace("quality=70", "quality=85");
                }

                episode.Images = new CrunchyrollEpisodeImages
                {
                    Thumbnail = new List<List<CrunchyrollImage>>
                    {
                        new List<CrunchyrollImage>
                        {
                            new CrunchyrollImage
                            {
                                Source = thumbnailUrl,
                                Width = 1920,
                                Height = 1080,
                                Type = "thumbnail"
                            }
                        }
                    }
                };
            }

            // Extract duration
            var durationMatch = EpisodeDurationRegex().Match(cardHtml);
            if (durationMatch.Success)
            {
                var durationStr = durationMatch.Groups[1].Value;
                if (int.TryParse(durationStr.Replace("m", ""), out int minutes))
                {
                    episode.DurationMs = minutes * 60 * 1000;
                }
            }

            if (!string.IsNullOrEmpty(episode.Id))
            {
                episodes.Add(episode);
            }
        }
        
        return episodes;
    }

    /// <summary>
    /// Extracts episodes from erc-playable-card elements (newer structure).
    /// </summary>
    private static List<CrunchyrollEpisode> ExtractFromErcPlayableCards(MatchCollection matches, ILogger logger)
    {
        var episodes = new List<CrunchyrollEpisode>();
        
        foreach (Match match in matches)
        {
            var cardHtml = match.Value;
            var episode = new CrunchyrollEpisode();
            
            // Look for watch links
            var watchMatch = Regex.Match(cardHtml, @"href=""[^""]*?/watch/([A-Z0-9]+)(?:/([^""]+))?""", RegexOptions.IgnoreCase);
            if (watchMatch.Success)
            {
                episode.Id = watchMatch.Groups[1].Value;
            }
            
            // Look for title
            var titleMatch = Regex.Match(cardHtml, @"<(?:h[1-6]|span|a)[^>]*class=""[^""]*title[^""]*""[^>]*>([^<]+)</", RegexOptions.IgnoreCase);
            if (titleMatch.Success)
            {
                episode.Title = HttpUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
            }
            
            // Try to find episode number
            var epNumMatch = Regex.Match(cardHtml, @"(?:E|Episode|Episodio|Episódio)\s*(\d+)", RegexOptions.IgnoreCase);
            if (epNumMatch.Success)
            {
                episode.EpisodeNumber = epNumMatch.Groups[1].Value;
                if (int.TryParse(episode.EpisodeNumber, out int epNum))
                {
                    episode.EpisodeNumberInt = epNum;
                    episode.SequenceNumber = epNum;
                }
            }
            
            if (!string.IsNullOrEmpty(episode.Id))
            {
                episodes.Add(episode);
                logger.LogDebug("[ERC] Found episode: {Id} - {Title}", episode.Id, episode.Title);
            }
        }
        
        return episodes;
    }

    /// <summary>
    /// Extracts episodes from __NEXT_DATA__ JSON embedded in the HTML.
    /// </summary>
    private static List<CrunchyrollEpisode> ExtractFromNextDataJson(string html, ILogger logger)
    {
        var episodes = new List<CrunchyrollEpisode>();
        
        try
        {
            var jsonMatch = Regex.Match(html, @"<script[^>]*id=""__NEXT_DATA__""[^>]*>([^<]+)</script>", RegexOptions.IgnoreCase);
            if (!jsonMatch.Success)
            {
                jsonMatch = Regex.Match(html, @"__NEXT_DATA__\s*=\s*({[^;]+});", RegexOptions.IgnoreCase);
            }
            
            if (jsonMatch.Success)
            {
                var jsonContent = jsonMatch.Groups[1].Value;
                logger.LogInformation("[NEXT_DATA] Found JSON block, length: {Length}", jsonContent.Length);
                
                // Extract episode IDs and titles using regex patterns within the JSON
                var episodeDataMatches = Regex.Matches(jsonContent, @"""id""\s*:\s*""([A-Z0-9]+)""\s*,\s*""title""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                
                foreach (Match epMatch in episodeDataMatches)
                {
                    var episode = new CrunchyrollEpisode
                    {
                        Id = epMatch.Groups[1].Value,
                        Title = HttpUtility.HtmlDecode(epMatch.Groups[2].Value)
                    };
                    
                    // Try to find episode number near this match
                    var contextStart = Math.Max(0, epMatch.Index - 200);
                    var contextLength = Math.Min(400, jsonContent.Length - contextStart);
                    var context = jsonContent.Substring(contextStart, contextLength);
                    
                    var epNumMatch = Regex.Match(context, @"""(?:episode|episodeNumber|sequence_number)""\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (epNumMatch.Success)
                    {
                        episode.EpisodeNumber = epNumMatch.Groups[1].Value;
                        if (int.TryParse(episode.EpisodeNumber, out int epNum))
                        {
                            episode.EpisodeNumberInt = epNum;
                            episode.SequenceNumber = epNum;
                        }
                    }
                    
                    episodes.Add(episode);
                }
                
                logger.LogInformation("[NEXT_DATA] Extracted {Count} episodes from JSON", episodes.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[NEXT_DATA] Failed to extract from JSON");
        }
        
        return episodes;
    }

    /// <summary>
    /// Extracts episodes from __INITIAL_STATE__ JSON embedded in the HTML.
    /// </summary>
    private static List<CrunchyrollEpisode> ExtractFromInitialStateJson(string html, ILogger logger)
    {
        var episodes = new List<CrunchyrollEpisode>();
        
        try
        {
            var jsonMatch = Regex.Match(html, @"__INITIAL_STATE__\s*=\s*({.+?})(?:;\s*</script>|$)", RegexOptions.Singleline);
            
            if (jsonMatch.Success)
            {
                var jsonContent = jsonMatch.Groups[1].Value;
                logger.LogInformation("[INITIAL_STATE] Found JSON block, length: {Length}", jsonContent.Length);
                
                // Look for episode objects in the JSON
                var episodeMatches = Regex.Matches(jsonContent, @"""([A-Z0-9]{9,})""\s*:\s*\{[^}]*""type""\s*:\s*""episode""[^}]*\}", RegexOptions.IgnoreCase);
                
                foreach (Match epMatch in episodeMatches)
                {
                    var episodeBlock = epMatch.Value;
                    var episode = new CrunchyrollEpisode
                    {
                        Id = epMatch.Groups[1].Value
                    };
                    
                    // Extract title
                    var titleMatch = Regex.Match(episodeBlock, @"""title""\s*:\s*""([^""]+)""");
                    if (titleMatch.Success)
                    {
                        episode.Title = HttpUtility.HtmlDecode(titleMatch.Groups[1].Value);
                    }
                    
                    // Extract episode number
                    var epNumMatch = Regex.Match(episodeBlock, @"""(?:episode_number|sequence_number)""\s*:\s*(\d+)");
                    if (epNumMatch.Success)
                    {
                        episode.EpisodeNumber = epNumMatch.Groups[1].Value;
                        if (int.TryParse(episode.EpisodeNumber, out int epNum))
                        {
                            episode.EpisodeNumberInt = epNum;
                            episode.SequenceNumber = epNum;
                        }
                    }
                    
                    episodes.Add(episode);
                }
                
                logger.LogInformation("[INITIAL_STATE] Extracted {Count} episodes from JSON", episodes.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[INITIAL_STATE] Failed to extract from JSON");
        }
        
        return episodes;
    }

    /// <summary>
    /// Result of extracting season info from the Crunchyroll series page HTML.
    /// The modern Crunchyroll SPA embeds season metadata in a &lt;h4&gt; element
    /// even though episodes are loaded client-side via JavaScript.
    /// </summary>
    public struct SeasonHtmlInfo
    {
        public string SeriesId;
        public string CurrentSeasonId;
        public string SeasonTitle;
    }

    /// <summary>
    /// Extracts season info (seriesId, currentSeasonId, seasonTitle) from the Crunchyroll
    /// series page HTML. The modern CR frontend embeds these as attributes on an &lt;h4&gt; element
    /// inside the season navigation section, even though actual episode data is loaded via JS.
    /// </summary>
    /// <param name="html">The HTML content of the series page.</param>
    /// <param name="logger">Logger for debugging.</param>
    /// <returns>SeasonHtmlInfo if found, null otherwise.</returns>
    public static SeasonHtmlInfo? ExtractSeasonInfoFromHtml(string html, ILogger logger)
    {
        try
        {
            // The modern Crunchyroll page has a <h4> element with custom attributes:
            // <h4 ... seriesid="GY8V11X7Y" seasons="[object Object]" currentseasonid="GRZX22DMY"
            //     seasondisplaynumber="" seasontitle="Fate/stay night [Unlimited Blade Works]">
            var match = Regex.Match(html,
                @"<h4[^>]*\sseriesid=""([^""]+)""[^>]*\scurrentseasonid=""([^""]+)""[^>]*\sseasontitle=""([^""]+)""",
                RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var info = new SeasonHtmlInfo
                {
                    SeriesId = match.Groups[1].Value,
                    CurrentSeasonId = match.Groups[2].Value,
                    SeasonTitle = System.Web.HttpUtility.HtmlDecode(match.Groups[3].Value)
                };

                logger.LogInformation(
                    "[HTML Scraper] Extracted season info from <h4>: SeriesId={SeriesId}, CurrentSeasonId={SeasonId}, Title={Title}",
                    info.SeriesId, info.CurrentSeasonId, info.SeasonTitle);

                return info;
            }

            // Fallback: try matching with attributes in different order
            var seriesIdMatch = Regex.Match(html, @"<h4[^>]*\sseriesid=""([^""]+)""", RegexOptions.IgnoreCase);
            var seasonIdMatch = Regex.Match(html, @"<h4[^>]*\scurrentseasonid=""([^""]+)""", RegexOptions.IgnoreCase);
            var seasonTitleMatch = Regex.Match(html, @"<h4[^>]*\sseasontitle=""([^""]+)""", RegexOptions.IgnoreCase);

            if (seriesIdMatch.Success && seasonIdMatch.Success)
            {
                var info = new SeasonHtmlInfo
                {
                    SeriesId = seriesIdMatch.Groups[1].Value,
                    CurrentSeasonId = seasonIdMatch.Groups[1].Value,
                    SeasonTitle = seasonTitleMatch.Success
                        ? System.Web.HttpUtility.HtmlDecode(seasonTitleMatch.Groups[1].Value)
                        : "Season 1"
                };

                logger.LogInformation(
                    "[HTML Scraper] Extracted season info (fallback): SeriesId={SeriesId}, CurrentSeasonId={SeasonId}, Title={Title}",
                    info.SeriesId, info.CurrentSeasonId, info.SeasonTitle);

                return info;
            }

            logger.LogDebug("[HTML Scraper] No season info found in HTML <h4> element");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[HTML Scraper] Error extracting season info from HTML");
        }

        return null;
    }

    /// <summary>
    /// Fallback: Extracts episodes from generic /watch/ links in the HTML.
    /// </summary>
    private static List<CrunchyrollEpisode> ExtractFromWatchLinks(string html, ILogger logger)
    {
        var episodes = new List<CrunchyrollEpisode>();
        var seenIds = new HashSet<string>();
        
        try
        {
            // Find all /watch/ID links
            var watchMatches = Regex.Matches(html, @"href=""[^""]*?/watch/([A-Z0-9]{9,})(?:/([^""]+))?""[^>]*>", RegexOptions.IgnoreCase);
            
            logger.LogInformation("[WatchLinks] Found {Count} total watch links, processing...", watchMatches.Count);
            
            foreach (Match match in watchMatches)
            {
                var episodeId = match.Groups[1].Value;
                
                if (seenIds.Contains(episodeId))
                    continue;
                    
                seenIds.Add(episodeId);
                
                var episode = new CrunchyrollEpisode
                {
                    Id = episodeId
                };
                
                // Try to extract slug title and episode number from it
                if (match.Groups[2].Success)
                {
                    var slug = match.Groups[2].Value;
                    
                    // Try multiple patterns to extract episode number from slug
                    // Common patterns:
                    // - "e1-title-here" or "e01-title-here"
                    // - "episode-1-title-here"
                    // - "1-title-here" (just number at start)
                    // - "title-here-1" (number at end, less common)
                    // - "s1e1-title" or "s01e01-title"
                    
                    string? extractedNumber = null;
                    string cleanTitle = slug;
                    
                    // Pattern 1: e1, e01, e001 at start
                    var eNumMatch = Regex.Match(slug, @"^e(\d+)-(.+)$", RegexOptions.IgnoreCase);
                    if (eNumMatch.Success)
                    {
                        extractedNumber = eNumMatch.Groups[1].Value.TrimStart('0');
                        if (string.IsNullOrEmpty(extractedNumber)) extractedNumber = "0";
                        cleanTitle = eNumMatch.Groups[2].Value;
                    }
                    
                    // Pattern 2: episode-1 at start
                    if (extractedNumber == null)
                    {
                        var epMatch = Regex.Match(slug, @"^episode-?(\d+)-(.+)$", RegexOptions.IgnoreCase);
                        if (epMatch.Success)
                        {
                            extractedNumber = epMatch.Groups[1].Value.TrimStart('0');
                            if (string.IsNullOrEmpty(extractedNumber)) extractedNumber = "0";
                            cleanTitle = epMatch.Groups[2].Value;
                        }
                    }
                    
                    // Pattern 3: s1e1 or s01e01 at start
                    if (extractedNumber == null)
                    {
                        var seMatch = Regex.Match(slug, @"^s\d+e(\d+)-(.+)$", RegexOptions.IgnoreCase);
                        if (seMatch.Success)
                        {
                            extractedNumber = seMatch.Groups[1].Value.TrimStart('0');
                            if (string.IsNullOrEmpty(extractedNumber)) extractedNumber = "0";
                            cleanTitle = seMatch.Groups[2].Value;
                        }
                    }
                    
                    // Pattern 4: Just number at start (1-title)
                    if (extractedNumber == null)
                    {
                        var numMatch = Regex.Match(slug, @"^(\d+)-(.+)$");
                        if (numMatch.Success)
                        {
                            extractedNumber = numMatch.Groups[1].Value.TrimStart('0');
                            if (string.IsNullOrEmpty(extractedNumber)) extractedNumber = "0";
                            cleanTitle = numMatch.Groups[2].Value;
                        }
                    }
                    
                    // Set episode number if found
                    if (!string.IsNullOrEmpty(extractedNumber))
                    {
                        episode.EpisodeNumber = extractedNumber;
                        if (int.TryParse(extractedNumber, out int epNum))
                        {
                            episode.EpisodeNumberInt = epNum;
                            episode.SequenceNumber = epNum;
                        }
                        logger.LogDebug("[WatchLinks] Extracted E{Num} from slug: {Slug}", extractedNumber, slug);
                    }
                    
                    // Convert slug to readable title
                    episode.Title = HttpUtility.UrlDecode(cleanTitle)
                        .Replace("-", " ")
                        .Trim();
                }
                
                // If still no episode number, try context around the link
                if (string.IsNullOrEmpty(episode.EpisodeNumber))
                {
                    var contextStart = Math.Max(0, match.Index - 150);
                    var contextEnd = Math.Min(html.Length, match.Index + match.Length + 300);
                    var context = html.Substring(contextStart, contextEnd - contextStart);
                    
                    // Try various patterns in context
                    var patterns = new[]
                    {
                        @"(?:E|Ep|Episode|Episódio|Episodio)\s*[:\s]?\s*(\d+)",
                        @">\s*(\d+)\s*<",  // Number between tags
                        @"episode[_-]?number["":\s]+(\d+)",  // JSON-like
                    };
                    
                    foreach (var pattern in patterns)
                    {
                        var epNumMatch = Regex.Match(context, pattern, RegexOptions.IgnoreCase);
                        if (epNumMatch.Success)
                        {
                            episode.EpisodeNumber = epNumMatch.Groups[1].Value;
                            if (int.TryParse(episode.EpisodeNumber, out int epNum))
                            {
                                episode.EpisodeNumberInt = epNum;
                                episode.SequenceNumber = epNum;
                            }
                            logger.LogDebug("[WatchLinks] Found E{Num} in context for {Id}", episode.EpisodeNumber, episodeId);
                            break;
                        }
                    }
                }
                
                episodes.Add(episode);
            }
            
            // Sort episodes by episode number if available
            episodes = episodes
                .OrderBy(e => e.EpisodeNumberInt ?? 9999)
                .ToList();
            
            // If no episode numbers were found but we have episodes, assign sequential numbers
            if (episodes.Any() && episodes.All(e => string.IsNullOrEmpty(e.EpisodeNumber)))
            {
                logger.LogWarning("[WatchLinks] No episode numbers found, assigning sequential numbers...");
                for (int i = 0; i < episodes.Count; i++)
                {
                    episodes[i].EpisodeNumber = (i + 1).ToString();
                    episodes[i].EpisodeNumberInt = i + 1;
                    episodes[i].SequenceNumber = i + 1;
                }
            }
            
            logger.LogInformation("[WatchLinks] Found {Count} unique episode links", episodes.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[WatchLinks] Failed to extract from watch links");
        }
        
        return episodes;
    }

    /// <summary>
    /// Extracts search results from Crunchyroll search page HTML.
    /// </summary>
    /// <param name="html">The HTML content.</param>
    /// <param name="logger">Logger for debugging.</param>
    /// <returns>List of search items.</returns>
    public static List<CrunchyrollSearchItem> ExtractSearchResultsFromHtml(string html, ILogger logger)
    {
        var results = new List<CrunchyrollSearchItem>();

        try
        {
            // Match series cards in search results
            var cardMatches = SeriesCardRegex().Matches(html);

            foreach (Match match in cardMatches)
            {
                var cardHtml = match.Value;
                var item = new CrunchyrollSearchItem();

                // Extract series ID from link
                var linkMatch = SeriesLinkRegex().Match(cardHtml);
                if (linkMatch.Success)
                {
                    item.Id = linkMatch.Groups[1].Value;
                    item.SlugTitle = linkMatch.Groups[2].Value;
                }

                // Extract title
                var titleMatch = SeriesTitleInCardRegex().Match(cardHtml);
                if (titleMatch.Success)
                {
                    item.Title = HttpUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                }

                // Extract poster image
                var imageMatch = SeriesImageInCardRegex().Match(cardHtml);
                if (imageMatch.Success)
                {
                    item.Images = new CrunchyrollImages
                    {
                        PosterTall = new List<List<CrunchyrollImage>>
                        {
                            new List<CrunchyrollImage>
                            {
                                new CrunchyrollImage
                                {
                                    Source = imageMatch.Groups[1].Value,
                                    Width = 480,
                                    Height = 720,
                                    Type = "poster_tall"
                                }
                            }
                        }
                    };
                }

                if (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Title))
                {
                    results.Add(item);
                }
            }

            logger.LogDebug("Extracted {Count} search results from HTML", results.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error extracting search results from HTML");
        }

        return results;
    }

    // Regex patterns for extracting data
    [GeneratedRegex(@"<h1[^>]*class=""[^""]*heading[^""]*""[^>]*>([^<]+)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<meta[^>]*property=""og:title""[^>]*content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex(@"<p[^>]*class=""[^""]*text--gq6o-[^""]*text--is-l[^""]*""[^>]*>([^<]+)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"<meta[^>]*property=""og:description""[^>]*content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgDescriptionRegex();

    [GeneratedRegex(@"<meta[^>]*property=""og:image""[^>]*content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PosterImageRegex();

    [GeneratedRegex(@"/series/[A-Z0-9]+/([a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SlugTitleRegex();

    [GeneratedRegex(@"<div[^>]*class=""[^""]*playable-card[^""]*""[^>]*data-t=""episode-card[^""]*""[^>]*>.*?</div>\s*</div>\s*</div>\s*</div>\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EpisodeCardRegex();

    [GeneratedRegex(@"<div[^>]*class=""[^""]*erc-playable-card[^""]*""[^>]*>.*?</div>\s*</div>\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ErcPlayableCardRegex();


    [GeneratedRegex(@"data-t=""description""[^>]*>([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeDescriptionRegex();

    [GeneratedRegex(@"<img[^>]*class=""[^""]*content-image__image[^""]*""[^>]*src=""(https://imgsrv\.crunchyroll\.com[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeThumbnailRegex();

    [GeneratedRegex(@"data-t=""duration-info""[^>]*>(\d+m)<", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeDurationRegex();

    [GeneratedRegex(@"<div[^>]*class=""[^""]*browse-card[^""]*""[^>]*>.*?</div>\s*</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SeriesCardRegex();

    [GeneratedRegex(@"href=""[^""]*?/series/([A-Z0-9]+)/([a-z0-9-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesLinkRegex();

    [GeneratedRegex(@"<h4[^>]*>([^<]+)</h4>", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesTitleInCardRegex();

    [GeneratedRegex(@"<img[^>]*src=""(https://imgsrv\.crunchyroll\.com[^""]+)""[^>]*alt=""", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesImageInCardRegex();
}
