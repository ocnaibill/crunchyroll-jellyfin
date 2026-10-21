using Jellyfin.Plugin.Crunchyroll.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Services;

/// <summary>
/// Service for mapping episodes between Jellyfin organization and Crunchyroll numbering.
/// Handles cases where:
/// 1. Crunchyroll uses continuous episode numbering across seasons
/// 2. Jellyfin has each season starting at episode 1
/// </summary>
public class EpisodeMappingService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeMappingService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public EpisodeMappingService(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates the episode mapping for all seasons of a series.
    /// </summary>
    /// <param name="seriesId">The Crunchyroll series ID.</param>
    /// <param name="seasons">List of Crunchyroll seasons.</param>
    /// <param name="allEpisodes">Dictionary of season ID to episodes.</param>
    /// <returns>The season mapping with episode offsets.</returns>
    public SeasonMapping CalculateSeasonMapping(
        string seriesId,
        List<CrunchyrollSeason> seasons,
        Dictionary<string, List<CrunchyrollEpisode>> allEpisodes)
    {
        var mapping = new SeasonMapping
        {
            CrunchyrollSeriesId = seriesId
        };

        // Sort seasons by sequence number
        var sortedSeasons = seasons
            .Where(s => !string.IsNullOrEmpty(s.Id))
            .OrderBy(s => s.SeasonSequenceNumber)
            .ThenBy(s => s.SeasonNumber)
            .ToList();

        foreach (var season in sortedSeasons)
        {
            if (string.IsNullOrEmpty(season.Id))
            {
                continue;
            }

            // Get episodes for this season
            var seasonEpisodes = allEpisodes.GetValueOrDefault(season.Id, new List<CrunchyrollEpisode>());
            
            // Check if this is a movie/special - map to Season 0 (Specials) instead of regular season
            // This fixes Issue #1 where Blue Lock movie was incorrectly mapped as Season 2
            if (IsMovieOrSpecial(season, allEpisodes))
            {
                var specialEntry = new SeasonMappingEntry
                {
                    JellyfinSeasonNumber = 0, // Season 0 = Specials folder in Jellyfin
                    CrunchyrollSeasonId = season.Id,
                    CrunchyrollSeasonNumber = season.SeasonNumber,
                    CrunchyrollSeasonTitle = season.Title,
                    EpisodeOffset = 0
                };
                
                mapping.Seasons.Add(specialEntry);

                _logger.LogDebug(
                    "Movie/special mapping: Jellyfin S0 (Specials) -> Crunchyroll ({Title}, ID: {Id})",
                    season.Title,
                    season.Id);
                
                continue;
            }

            // Calculate episode offset for regular seasons
            // Use the real SeasonNumber from Crunchyroll instead of sequential numbering
            // This fixes Issue #2 where missing seasons (geo-blocked) caused incorrect mapping
            // Use SeasonSequenceNumber for Jellyfin mapping (not SeasonNumber!)
            // Crunchyroll sets SeasonNumber=1 for ALL seasons within a series,
            // but SeasonSequenceNumber gives the actual order (1, 2, 3...).
            // Example: Frieren S1 has season_number=1, season_sequence_number=1
            //          Frieren S2 has season_number=1, season_sequence_number=2
            //
            // IMPORTANT: Some series (e.g. Lord El-Melloi II) have SeasonSequenceNumber=0
            // for the main season. In Jellyfin, S0 is reserved for Specials, so we must
            // bump SeasonSequenceNumber=0 to S1 for regular (non-special) seasons.
            // BUT: Only do this if no other regular season already occupies S1,
            // to avoid collisions when there are two regular seasons (SeqNum=0 and SeqNum=1).
            int jellyfinSeasonNum = season.SeasonSequenceNumber;

            // NEW: Try to extract the true season number from the Title.
            // Handles both "Season 3"/"Temporada 3" and the Portuguese ordinal
            // form "3ª Temporada" (digit before the word).
            if (!string.IsNullOrEmpty(season.Title))
            {
                var titleMatch = System.Text.RegularExpressions.Regex.Match(
                    season.Title,
                    @"(?i)(?:season|temporada)\s*(?<after>\d+)\b|(?<before>\d+)\s*[ªº°]?\s*(?:season|temporada)\b");
                if (titleMatch.Success)
                {
                    var digits = titleMatch.Groups["after"].Success
                        ? titleMatch.Groups["after"].Value
                        : titleMatch.Groups["before"].Value;
                    if (int.TryParse(digits, out int extractedSeason))
                    {
                        jellyfinSeasonNum = extractedSeason;
                        _logger.LogDebug(
                            "Season '{Title}' matched regex, overriding Jellyfin season number from {Original} to {Extracted}",
                            season.Title, season.SeasonSequenceNumber, extractedSeason);
                    }
                }
                else if (season.Title.Contains("Culling", StringComparison.OrdinalIgnoreCase) || season.Title.Contains("Jogo do Abate", StringComparison.OrdinalIgnoreCase))
                {
                    jellyfinSeasonNum = 3;
                    _logger.LogDebug(
                        "Season '{Title}' matched 'Culling Game/Jogo do Abate', overriding Jellyfin season number from {Original} to 3",
                        season.Title, season.SeasonSequenceNumber);
                }
                else if (season.Title.Contains("Shibuya", StringComparison.OrdinalIgnoreCase) || season.Title.Contains("Hidden Inventory", StringComparison.OrdinalIgnoreCase) || season.Title.Contains("Inventário", StringComparison.OrdinalIgnoreCase))
                {
                    jellyfinSeasonNum = 2;
                    _logger.LogDebug(
                        "Season '{Title}' matched 'Shibuya/Hidden Inventory', overriding Jellyfin season number from {Original} to 2",
                        season.Title, season.SeasonSequenceNumber);
                }
            }
            if (jellyfinSeasonNum == 0)
            {
                bool s1AlreadyTaken = sortedSeasons.Any(s =>
                    s.Id != season.Id
                    && s.SeasonSequenceNumber == 1
                    && !IsMovieOrSpecial(s, allEpisodes));

                if (!s1AlreadyTaken)
                {
                    jellyfinSeasonNum = 1;
                    _logger.LogDebug(
                        "Season '{Title}' has SeasonSequenceNumber=0, remapping to Jellyfin S1 (S0 is reserved for Specials)",
                        season.Title);
                }
                else
                {
                    _logger.LogWarning(
                        "Season '{Title}' has SeasonSequenceNumber=0 but S1 is already taken by another regular season. Keeping as S0.",
                        season.Title);
                }
            }
            int episodeOffset = CalculateEpisodeOffset(seasonEpisodes, jellyfinSeasonNum);

            var entry = new SeasonMappingEntry
            {
                JellyfinSeasonNumber = jellyfinSeasonNum,
                CrunchyrollSeasonId = season.Id,
                CrunchyrollSeasonNumber = season.SeasonNumber,
                CrunchyrollSeasonTitle = season.Title,
                EpisodeOffset = episodeOffset
            };

            mapping.Seasons.Add(entry);

            _logger.LogInformation(
                "Season mapping: Jellyfin S{JellyfinSeason} -> Crunchyroll S{CrunchyrollSeason} seq={SeqNum} ({Title}), Offset: {Offset}",
                jellyfinSeasonNum,
                season.SeasonNumber,
                season.SeasonSequenceNumber,
                season.Title,
                episodeOffset);
        }

        return mapping;
    }

    /// <summary>
    /// Determines if a season is actually a movie or special that should be excluded from season mapping.
    /// </summary>
    /// <param name="season">The season to check.</param>
    /// <param name="allEpisodes">Dictionary of season ID to episodes.</param>
    /// <returns>True if the season is a movie or special.</returns>
    private bool IsMovieOrSpecial(CrunchyrollSeason season, Dictionary<string, List<CrunchyrollEpisode>> allEpisodes)
    {
        var title = season.Title?.ToLowerInvariant() ?? string.Empty;

        // Check for movie/film keywords in title (whole words only)
        var movieKeywords = new[] { "movie", "film", "filme", "the movie", "劇場版", "gekijouban" };
        if (ContainsWholeWordKeyword(title, movieKeywords))
        {
            return true;
        }

        // Check for special/OVA keywords in title (whole words only, so a season
        // titled e.g. "Overload" is NOT matched by the substring "oad")
        var specialKeywords = new[] { "ova", "ovas", "oad", "oads", "special", "specials", "especial", "especiais", "スペシャル" };
        if (ContainsWholeWordKeyword(title, specialKeywords))
        {
            return true;
        }

        // Check episode count - movies typically have only 1 episode
        var episodes = allEpisodes.GetValueOrDefault(season.Id ?? string.Empty, new List<CrunchyrollEpisode>());
        if (episodes.Count == 1)
        {
            // Single episode with long duration (> 60 min) is likely a movie
            var episode = episodes.First();
            if (episode.DurationMs > 60 * 60 * 1000) // 60 minutes in milliseconds
            {
                return true;
            }
            
            // Also check if the episode title contains movie keywords
            var epTitle = episode.Title?.ToLowerInvariant() ?? string.Empty;
            if (ContainsWholeWordKeyword(epTitle, movieKeywords))
            {
                return true;
            }
        }

        // Check season keywords if available
        if (season.Keywords != null)
        {
            var keywordCheckList = new[] { "movie", "film", "ova", "special" };
            if (season.Keywords.Any(k => keywordCheckList.Contains(k.ToLowerInvariant())))
            {
                return true;
            }
        }

        // Check for "Series: Episode/Character Name" pattern (common for Japanese movies)
        // Example: "Blue Lock: Episode Nagi" where "Episode" refers to a character, not an episode number
        if (title.Contains(':') && episodes.Count <= 1)
        {
            // If it has a colon and only 1 or 0 episodes, check if it might be a movie
            var afterColon = title.Split(':').LastOrDefault()?.Trim() ?? string.Empty;
            
            // Check if the part after colon starts with "episode" (but not followed by a number)
            // This catches cases like "Episode Nagi" vs "Episode 1"
            if (afterColon.StartsWith("episode", StringComparison.OrdinalIgnoreCase))
            {
                var afterEpisode = afterColon.Substring("episode".Length).TrimStart();
                // If what follows "episode" is NOT a number, it's likely a movie title
                if (!string.IsNullOrEmpty(afterEpisode) && !char.IsDigit(afterEpisode[0]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether the text contains any of the keywords as a WHOLE word
    /// (regex word boundaries), matching an optional trailing "s" so both
    /// "OVA" and "OVAs" are caught. This avoids false positives where the
    /// keyword appears inside a normal word (e.g. "Overload" contains "oad").
    /// </summary>
    private static bool ContainsWholeWordKeyword(string text, IEnumerable<string> keywords)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(keyword)}\b";
            if (System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Calculates the episode offset for a season.
    /// </summary>
    /// <param name="episodes">The episodes in the season.</param>
    /// <param name="jellyfinSeasonNumber">The Jellyfin season number.</param>
    /// <returns>The offset to add to Jellyfin episode numbers to get Crunchyroll episode numbers.</returns>
    private int CalculateEpisodeOffset(List<CrunchyrollEpisode> episodes, int jellyfinSeasonNumber)
    {
        if (episodes.Count == 0)
        {
            return 0;
        }

        // Find the first episode (by sequence number)
        // First try matching by SeasonNumber, but fall back to all episodes
        // since Crunchyroll often has SeasonNumber=1 for all seasons
        var firstEpisode = episodes
            .Where(e => e.SeasonNumber == jellyfinSeasonNumber)
            .OrderBy(e => e.SequenceNumber)
            .FirstOrDefault();

        // Fallback: if no episodes match the season number filter (common when
        // CR uses season_number=1 for all seasons), just use the first episode
        if (firstEpisode == null)
        {
            firstEpisode = episodes
                .OrderBy(e => e.SequenceNumber)
                .FirstOrDefault();
        }

        if (firstEpisode == null || !firstEpisode.EpisodeNumberInt.HasValue)
        {
            return 0;
        }

        int crunchyrollFirstEpisode = firstEpisode.EpisodeNumberInt.Value;

        // Calculate offset: if CR starts at 25 and Jellyfin expects 1,
        // offset is 24 (CR_number = Jellyfin_number + offset)
        int offset = crunchyrollFirstEpisode - 1;

        _logger.LogDebug(
            "Detected episode offset for season {Season}: Crunchyroll starts at EP{CrEp}, offset = {Offset}",
            jellyfinSeasonNumber,
            crunchyrollFirstEpisode,
            offset);

        return offset;
    }

    /// <summary>
    /// Finds the matching Crunchyroll episode for a Jellyfin episode.
    /// </summary>
    /// <param name="jellyfinSeasonNumber">The Jellyfin season number.</param>
    /// <param name="jellyfinEpisodeNumber">The Jellyfin episode number.</param>
    /// <param name="seasonMapping">The season mapping configuration.</param>
    /// <param name="allEpisodes">Dictionary of season ID to episodes.</param>
    /// <returns>The matching result.</returns>
    public EpisodeMatchResult FindMatchingEpisode(
        int jellyfinSeasonNumber,
        int jellyfinEpisodeNumber,
        SeasonMapping seasonMapping,
        Dictionary<string, List<CrunchyrollEpisode>> allEpisodes)
    {
        _logger.LogDebug(
            "Finding match for Jellyfin S{Season}E{Episode}",
            jellyfinSeasonNumber,
            jellyfinEpisodeNumber);

        // Find ALL season mapping entries for this Jellyfin season.
        // Several Crunchyroll seasons can legitimately map to the same Jellyfin
        // season (e.g. multiple movie/special entries all mapped to S0), so we
        // must try each entry instead of blindly using the first one.
        var seasonEntries = seasonMapping.Seasons
            .Where(s => s.JellyfinSeasonNumber == jellyfinSeasonNumber
                && !string.IsNullOrEmpty(s.CrunchyrollSeasonId))
            .ToList();

        if (seasonEntries.Count == 0)
        {
            _logger.LogWarning(
                "No mapping found for Jellyfin season {Season}",
                jellyfinSeasonNumber);

            return new EpisodeMatchResult
            {
                Success = false,
                Confidence = 0,
                Notes = $"No mapping found for season {jellyfinSeasonNumber}"
            };
        }

        EpisodeMatchResult? lastFailure = null;

        foreach (var seasonEntry in seasonEntries)
        {
            var result = TryMatchEpisodeInSeason(
                seasonEntry,
                jellyfinSeasonNumber,
                jellyfinEpisodeNumber,
                allEpisodes);

            if (result.Success)
            {
                return result;
            }

            lastFailure = result;
        }

        return lastFailure ?? new EpisodeMatchResult
        {
            Success = false,
            Confidence = 0,
            Notes = $"No matching episode found for S{jellyfinSeasonNumber}E{jellyfinEpisodeNumber}"
        };
    }

    /// <summary>
    /// Attempts to find the matching Crunchyroll episode inside a single mapped season entry.
    /// </summary>
    private EpisodeMatchResult TryMatchEpisodeInSeason(
        SeasonMappingEntry seasonEntry,
        int jellyfinSeasonNumber,
        int jellyfinEpisodeNumber,
        Dictionary<string, List<CrunchyrollEpisode>> allEpisodes)
    {
        // Get episodes for the matched season
        var episodes = allEpisodes.GetValueOrDefault(seasonEntry.CrunchyrollSeasonId!, new List<CrunchyrollEpisode>());

        if (episodes.Count == 0)
        {
            return new EpisodeMatchResult
            {
                Success = false,
                Confidence = 0,
                Notes = $"No episodes found in the mapped Crunchyroll season '{seasonEntry.CrunchyrollSeasonTitle}'"
            };
        }

        // Calculate the expected Crunchyroll episode number
        int expectedCrunchyrollEpisode = jellyfinEpisodeNumber + seasonEntry.EpisodeOffset;

        _logger.LogInformation(
            "Expected Crunchyroll episode: {Expected} (Jellyfin {JellyfinEp} + offset {Offset})",
            expectedCrunchyrollEpisode,
            jellyfinEpisodeNumber,
            seasonEntry.EpisodeOffset);

        // Try to find exact match by episode number
        var matchedEpisode = episodes
            .FirstOrDefault(e => e.EpisodeNumberInt == expectedCrunchyrollEpisode);

        if (matchedEpisode != null)
        {
            return new EpisodeMatchResult
            {
                Success = true,
                Episode = matchedEpisode,
                Confidence = 100,
                Notes = "Matched by episode number with offset calculation"
            };
        }

        // Try matching by sequence within the season (1st episode = sequence 1, etc.)
        var sortedEpisodes = episodes
            .Where(e => !e.IsClip)
            .OrderBy(e => e.SequenceNumber)
            .ToList();

        if (jellyfinEpisodeNumber > 0 && jellyfinEpisodeNumber <= sortedEpisodes.Count)
        {
            matchedEpisode = sortedEpisodes[jellyfinEpisodeNumber - 1];
            
            return new EpisodeMatchResult
            {
                Success = true,
                Episode = matchedEpisode,
                Confidence = 80,
                Notes = "Matched by position within season (sequence order)"
            };
        }

        // Try fuzzy matching by episode string (handles cases like "24.5", "OVA", etc.)
        var episodeString = jellyfinEpisodeNumber.ToString();
        matchedEpisode = episodes
            .FirstOrDefault(e => e.EpisodeNumber == episodeString);

        if (matchedEpisode != null)
        {
            return new EpisodeMatchResult
            {
                Success = true,
                Episode = matchedEpisode,
                Confidence = 70,
                Notes = "Matched by episode string"
            };
        }

        return new EpisodeMatchResult
        {
            Success = false,
            Confidence = 0,
            Notes = $"No matching episode found for S{jellyfinSeasonNumber}E{jellyfinEpisodeNumber}"
        };
    }

    /// <summary>
    /// Attempts to find the best matching season when the user's organization doesn't match Crunchyroll's.
    /// </summary>
    /// <param name="jellyfinSeasonNumber">The Jellyfin season number.</param>
    /// <param name="seriesTitle">The series title for searching.</param>
    /// <param name="seasons">All available Crunchyroll seasons.</param>
    /// <param name="preferredAudioLocale">The preferred audio locale (e.g., "ja-JP" for Japanese).</param>
    /// <returns>The best matching season or null.</returns>
    public CrunchyrollSeason? FindBestMatchingSeason(
        int jellyfinSeasonNumber,
        string seriesTitle,
        List<CrunchyrollSeason> seasons,
        string? preferredAudioLocale = "ja-JP")
    {
        if (seasons.Count == 0)
        {
            return null;
        }

        // Filter to prefer original Japanese audio versions
        var preferredSeasons = seasons
            .Where(s => s.AudioLocales?.Contains(preferredAudioLocale ?? "ja-JP") == true)
            .ToList();

        // If no preferred language versions, use all
        var candidateSeasons = preferredSeasons.Count > 0 ? preferredSeasons : seasons;

        // Sort by season sequence number
        var sortedSeasons = candidateSeasons
            .OrderBy(s => s.SeasonSequenceNumber)
            .ThenBy(s => s.SeasonNumber)
            .ToList();

        // Direct match by position
        if (jellyfinSeasonNumber > 0 && jellyfinSeasonNumber <= sortedSeasons.Count)
        {
            return sortedSeasons[jellyfinSeasonNumber - 1];
        }

        // Fallback to the last season if requested season is higher
        return sortedSeasons.LastOrDefault();
    }
}
