using FluentAssertions;
using Jellyfin.Plugin.Crunchyroll.Models;
using Jellyfin.Plugin.Crunchyroll.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Crunchyroll.Tests.Services;

/// <summary>
/// Tests for the EpisodeMappingService.
/// These tests verify correct season and episode mapping between Jellyfin and Crunchyroll.
/// </summary>
public class EpisodeMappingServiceTests
{
    private readonly Mock<ILogger> _loggerMock;
    private readonly EpisodeMappingService _service;

    public EpisodeMappingServiceTests()
    {
        _loggerMock = new Mock<ILogger>();
        _service = new EpisodeMappingService(_loggerMock.Object);
    }

    /// <summary>
    /// Reproduces Issue #1: Blue Lock Season 2 incorrectly mapped to the movie.
    /// 
    /// Scenario:
    /// - Crunchyroll has: Season 1 → Movie → Season 2 (ordered by SeasonSequenceNumber)
    /// - User's Jellyfin has: Season 1 → Season 2
    /// - Current bug: Jellyfin S2 maps to Movie instead of Crunchyroll S2
    /// </summary>
    [Fact]
    public void CalculateSeasonMapping_BlueLockWithMovie_ShouldNotMapSeasonToMovie()
    {
        // Arrange - Blue Lock structure from Crunchyroll
        var seasons = new List<CrunchyrollSeason>
        {
            new CrunchyrollSeason
            {
                Id = "GRDQCDZWG",
                Title = "Blue Lock",
                SeasonNumber = 1,
                SeasonSequenceNumber = 1,
                NumberOfEpisodes = 24,
                AudioLocales = new List<string> { "ja-JP" }
            },
            new CrunchyrollSeason
            {
                Id = "G6K50DP28",
                Title = "Blue Lock: Episode Nagi", // This is a MOVIE
                SeasonNumber = 1, // Note: Movies often have SeasonNumber = 1
                SeasonSequenceNumber = 2,
                NumberOfEpisodes = 1,
                AudioLocales = new List<string> { "ja-JP" }
            },
            new CrunchyrollSeason
            {
                Id = "GRMG8Q25R",
                Title = "Blue Lock Season 2",
                SeasonNumber = 2,
                SeasonSequenceNumber = 3,
                NumberOfEpisodes = 14,
                AudioLocales = new List<string> { "ja-JP" }
            }
        };

        var allEpisodes = new Dictionary<string, List<CrunchyrollEpisode>>
        {
            ["GRDQCDZWG"] = CreateEpisodeList(1, 24),     // Season 1: 24 episodes
            ["G6K50DP28"] = CreateEpisodeList(1, 1),      // Movie: 1 "episode"
            ["GRMG8Q25R"] = CreateEpisodeList(1, 14)      // Season 2: 14 episodes
        };

        // Act
        var result = _service.CalculateSeasonMapping("G4PH0WEKE", seasons, allEpisodes);

        // Assert - After the fix, movie should be mapped to Season 0 (Specials)
        // So we should have 3 seasons: S0 (movie), S1, S2
        result.Seasons.Should().HaveCount(3);
        
        // Jellyfin Season 0 (Specials) → Crunchyroll Movie
        var season0 = result.Seasons.First(s => s.JellyfinSeasonNumber == 0);
        season0.CrunchyrollSeasonId.Should().Be("G6K50DP28");
        season0.CrunchyrollSeasonTitle.Should().Be("Blue Lock: Episode Nagi");
        
        // Jellyfin Season 1 → Crunchyroll Season 1
        var season1 = result.Seasons.First(s => s.JellyfinSeasonNumber == 1);
        season1.CrunchyrollSeasonId.Should().Be("GRDQCDZWG");
        season1.CrunchyrollSeasonTitle.Should().Be("Blue Lock");
        
        // Jellyfin Season 2 → Crunchyroll Season 2 (NOT the movie!)
        var season2 = result.Seasons.First(s => s.JellyfinSeasonNumber == 2);
        season2.CrunchyrollSeasonId.Should().Be("GRMG8Q25R", "Movie should map to S0, Season 2 should map to actual Season 2");
        season2.CrunchyrollSeasonTitle.Should().Be("Blue Lock Season 2");
    }

    /// <summary>
    /// Tests that seasons with only Japanese audio are correctly filtered and matched.
    /// </summary>
    [Fact]
    public void FindBestMatchingSeason_WithPreferredAudioLocale_ShouldFilterCorrectly()
    {
        // Arrange
        var seasons = new List<CrunchyrollSeason>
        {
            new CrunchyrollSeason
            {
                Id = "SEASON_JA",
                Title = "Season 1 (Japanese)",
                SeasonNumber = 1,
                SeasonSequenceNumber = 1,
                AudioLocales = new List<string> { "ja-JP" }
            },
            new CrunchyrollSeason
            {
                Id = "SEASON_EN",
                Title = "Season 1 (English Dub)",
                SeasonNumber = 1,
                SeasonSequenceNumber = 2,
                AudioLocales = new List<string> { "en-US" }
            }
        };

        // Act
        var result = _service.FindBestMatchingSeason(1, "Test Series", seasons, "ja-JP");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("SEASON_JA");
        result.AudioLocales.Should().Contain("ja-JP");
    }

    /// <summary>
    /// Tests that episode offset is correctly calculated for continuous numbering.
    /// Example: Season 2 on Crunchyroll starts at episode 25 (continuous from S1).
    /// </summary>
    [Fact]
    public void CalculateSeasonMapping_WithContinuousEpisodeNumbering_ShouldCalculateCorrectOffset()
    {
        // Arrange - Anime with continuous episode numbering
        var seasons = new List<CrunchyrollSeason>
        {
            new CrunchyrollSeason
            {
                Id = "SEASON_1",
                Title = "Season 1",
                SeasonNumber = 1,
                SeasonSequenceNumber = 1,
                NumberOfEpisodes = 24
            },
            new CrunchyrollSeason
            {
                Id = "SEASON_2",
                Title = "Season 2",
                SeasonNumber = 2,
                SeasonSequenceNumber = 2,
                NumberOfEpisodes = 24
            }
        };

        // Season 2 starts at episode 25 on Crunchyroll
        var allEpisodes = new Dictionary<string, List<CrunchyrollEpisode>>
        {
            ["SEASON_1"] = CreateEpisodeList(1, 24),
            ["SEASON_2"] = CreateEpisodeList(25, 24) // Starts at 25!
        };

        // Act
        var result = _service.CalculateSeasonMapping("SERIES_ID", seasons, allEpisodes);

        // Assert
        result.Seasons.Should().HaveCount(2);
        
        var season1 = result.Seasons.First(s => s.JellyfinSeasonNumber == 1);
        season1.EpisodeOffset.Should().Be(0); // No offset for S1
        
        var season2 = result.Seasons.First(s => s.JellyfinSeasonNumber == 2);
        season2.EpisodeOffset.Should().Be(24); // Offset of 24 (25 - 1)
    }

    /// <summary>
    /// Tests that FindMatchingEpisode correctly applies the episode offset.
    /// </summary>
    [Fact]
    public void FindMatchingEpisode_WithOffset_ShouldFindCorrectEpisode()
    {
        // Arrange
        var seasonMapping = new SeasonMapping
        {
            CrunchyrollSeriesId = "SERIES_ID",
            Seasons = new List<SeasonMappingEntry>
            {
                new SeasonMappingEntry
                {
                    JellyfinSeasonNumber = 2,
                    CrunchyrollSeasonId = "SEASON_2",
                    CrunchyrollSeasonNumber = 2,
                    EpisodeOffset = 24 // S2 starts at episode 25
                }
            }
        };

        var allEpisodes = new Dictionary<string, List<CrunchyrollEpisode>>
        {
            ["SEASON_2"] = CreateEpisodeList(25, 24)
        };

        // Act - User has S2E1 in Jellyfin, which is E25 on Crunchyroll
        var result = _service.FindMatchingEpisode(2, 1, seasonMapping, allEpisodes);

        // Assert
        result.Success.Should().BeTrue();
        result.Episode.Should().NotBeNull();
        result.Episode!.EpisodeNumberInt.Should().Be(25); // E1 + offset 24 = E25
        result.Confidence.Should().Be(100);
    }

    /// <summary>
    /// Tests Issue #2: Attack on Titan geo-blocking scenario.
    /// When Crunchyroll doesn't have Season 1 in user's region, S2 should still map to S2.
    /// </summary>
    [Fact]
    public void CalculateSeasonMapping_AttackOnTitan_GeoBlocking_ShouldMapBySeasonNumber()
    {
        // Arrange - Attack on Titan in a region where S1 is not available
        // CR only has: OADs, S2, S3, S4 (S1 is geo-blocked)
        var seasons = new List<CrunchyrollSeason>
        {
            // Season 1 is NOT available (geo-blocked)
            new CrunchyrollSeason
            {
                Id = "GR751KNZY_OADS",
                Title = "Attack on Titan OADs",
                SeasonNumber = 0, // OADs typically have SeasonNumber = 0
                SeasonSequenceNumber = 1,
                NumberOfEpisodes = 8,
                AudioLocales = new List<string> { "ja-JP" }
            },
            new CrunchyrollSeason
            {
                Id = "GR751KNZY_S2",
                Title = "Attack on Titan Season 2",
                SeasonNumber = 2, // Real season number from Crunchyroll
                SeasonSequenceNumber = 2,
                NumberOfEpisodes = 12,
                AudioLocales = new List<string> { "ja-JP" }
            },
            new CrunchyrollSeason
            {
                Id = "GR751KNZY_S3",
                Title = "Attack on Titan Season 3",
                SeasonNumber = 3,
                SeasonSequenceNumber = 3,
                NumberOfEpisodes = 22,
                AudioLocales = new List<string> { "ja-JP" }
            },
            new CrunchyrollSeason
            {
                Id = "GR751KNZY_S4",
                Title = "Attack on Titan Final Season",
                SeasonNumber = 4,
                SeasonSequenceNumber = 4,
                NumberOfEpisodes = 30,
                AudioLocales = new List<string> { "ja-JP" }
            }
        };

        var allEpisodes = new Dictionary<string, List<CrunchyrollEpisode>>
        {
            ["GR751KNZY_OADS"] = CreateEpisodeList(1, 8),  // OADs
            ["GR751KNZY_S2"] = CreateEpisodeList(1, 12),   // S2
            ["GR751KNZY_S3"] = CreateEpisodeList(1, 22),   // S3
            ["GR751KNZY_S4"] = CreateEpisodeList(1, 30)    // S4
        };

        // Act
        var result = _service.CalculateSeasonMapping("GR751KNZY", seasons, allEpisodes);

        // Assert - Mapping should be by real SeasonNumber, not sequential
        // OADs (SeasonNumber=0) -> S0
        // S2 (SeasonNumber=2) -> S2 (not S1!)
        // S3 (SeasonNumber=3) -> S3
        // S4 (SeasonNumber=4) -> S4
        
        // OADs with SeasonNumber=0 should map to Specials (S0) 
        // or be detected as special by IsMovieOrSpecial
        var oadsEntry = result.Seasons.FirstOrDefault(s => s.CrunchyrollSeasonTitle == "Attack on Titan OADs");
        oadsEntry.Should().NotBeNull();
        oadsEntry!.JellyfinSeasonNumber.Should().Be(0, "OADs should map to Season 0 (Specials)");
        
        // Season 2 should map to Jellyfin S2 (not S1!)
        var season2 = result.Seasons.First(s => s.JellyfinSeasonNumber == 2);
        season2.CrunchyrollSeasonId.Should().Be("GR751KNZY_S2");
        season2.CrunchyrollSeasonTitle.Should().Be("Attack on Titan Season 2");
        
        // Season 3 should map to Jellyfin S3
        var season3 = result.Seasons.First(s => s.JellyfinSeasonNumber == 3);
        season3.CrunchyrollSeasonId.Should().Be("GR751KNZY_S3");
        
        // Season 4 should map to Jellyfin S4
        var season4 = result.Seasons.First(s => s.JellyfinSeasonNumber == 4);
        season4.CrunchyrollSeasonId.Should().Be("GR751KNZY_S4");
        
        // Should NOT have Season 1 mapped (it's not available in CR)
        result.Seasons.Should().NotContain(s => s.JellyfinSeasonNumber == 1, 
            "Season 1 is not available in Crunchyroll (geo-blocked)");
    }

    /// <summary>
    /// Helper method to create a list of episodes for testing.
    /// </summary>
    private static List<CrunchyrollEpisode> CreateEpisodeList(int startingEpisode, int count)
    {
        return Enumerable.Range(startingEpisode, count)
            .Select(i => new CrunchyrollEpisode
            {
                Id = $"EP_{i}",
                EpisodeNumber = i.ToString(),
                EpisodeNumberInt = i,
                SequenceNumber = i,
                Title = $"Episode {i}"
            })
            .ToList();
    }
}
