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

        int jellyfinSeasonNumber = 1;

        foreach (var season in sortedSeasons)
        {
            if (string.IsNullOrEmpty(season.Id))
            {
                continue;
            }

            // Get episodes for this season
            var seasonEpisodes = allEpisodes.GetValueOrDefault(season.Id, new List<CrunchyrollEpisode>());
            
            // Calculate episode offset
            int episodeOffset = CalculateEpisodeOffset(seasonEpisodes, jellyfinSeasonNumber);

            var entry = new SeasonMappingEntry
            {
                JellyfinSeasonNumber = jellyfinSeasonNumber,
                CrunchyrollSeasonId = season.Id,
                CrunchyrollSeasonNumber = season.SeasonNumber,
                CrunchyrollSeasonTitle = season.Title,
                EpisodeOffset = episodeOffset
            };

            mapping.Seasons.Add(entry);

            _logger.LogDebug(
                "Season mapping: Jellyfin S{JellyfinSeason} -> Crunchyroll S{CrunchyrollSeason} ({Title}), Offset: {Offset}",
                jellyfinSeasonNumber,
                season.SeasonNumber,
                season.Title,
                episodeOffset);

            jellyfinSeasonNumber++;
        }

        return mapping;
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
        var firstEpisode = episodes
            .Where(e => e.EpisodeNumberInt.HasValue)
            .OrderBy(e => e.SequenceNumber)
            .FirstOrDefault();

        if (firstEpisode == null || !firstEpisode.EpisodeNumberInt.HasValue)
        {
            return 0;
        }

        int crunchyrollFirstEpisode = firstEpisode.EpisodeNumberInt.Value;
        
        // If Crunchyroll starts at 1 for this season, no offset needed
        if (crunchyrollFirstEpisode == 1)
        {
            return 0;
        }

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

        // Find the season mapping entry
        var seasonEntry = seasonMapping.Seasons
            .FirstOrDefault(s => s.JellyfinSeasonNumber == jellyfinSeasonNumber);

        if (seasonEntry == null || string.IsNullOrEmpty(seasonEntry.CrunchyrollSeasonId))
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

        // Get episodes for the matched season
        var episodes = allEpisodes.GetValueOrDefault(seasonEntry.CrunchyrollSeasonId, new List<CrunchyrollEpisode>());

        if (episodes.Count == 0)
        {
            return new EpisodeMatchResult
            {
                Success = false,
                Confidence = 0,
                Notes = "No episodes found in the mapped Crunchyroll season"
            };
        }

        // Calculate the expected Crunchyroll episode number
        int expectedCrunchyrollEpisode = jellyfinEpisodeNumber + seasonEntry.EpisodeOffset;

        _logger.LogDebug(
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
