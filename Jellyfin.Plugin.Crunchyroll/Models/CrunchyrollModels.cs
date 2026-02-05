using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Crunchyroll.Models;

/// <summary>
/// Crunchyroll API response wrapper.
/// </summary>
/// <typeparam name="T">The type of data in the response.</typeparam>
public class CrunchyrollResponse<T>
{
    /// <summary>
    /// Gets or sets the total count of items.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the data collection.
    /// </summary>
    [JsonPropertyName("data")]
    public List<T>? Data { get; set; }

    /// <summary>
    /// Gets or sets the metadata about the response.
    /// </summary>
    [JsonPropertyName("meta")]
    public CrunchyrollMeta? Meta { get; set; }
}

/// <summary>
/// Metadata about the API response.
/// </summary>
public class CrunchyrollMeta
{
    /// <summary>
    /// Gets or sets the versions available.
    /// </summary>
    [JsonPropertyName("versions_considered")]
    public bool VersionsConsidered { get; set; }
}

/// <summary>
/// Represents a Crunchyroll series.
/// </summary>
public class CrunchyrollSeries
{
    /// <summary>
    /// Gets or sets the series ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the external ID.
    /// </summary>
    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the series title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the slug for URL.
    /// </summary>
    [JsonPropertyName("slug_title")]
    public string? SlugTitle { get; set; }

    /// <summary>
    /// Gets or sets the series description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the extended description.
    /// </summary>
    [JsonPropertyName("extended_description")]
    public string? ExtendedDescription { get; set; }

    /// <summary>
    /// Gets or sets the keywords.
    /// </summary>
    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    /// <summary>
    /// Gets or sets the season tags.
    /// </summary>
    [JsonPropertyName("season_tags")]
    public List<string>? SeasonTags { get; set; }

    /// <summary>
    /// Gets or sets the images.
    /// </summary>
    [JsonPropertyName("images")]
    public CrunchyrollImages? Images { get; set; }

    /// <summary>
    /// Gets or sets the maturity ratings.
    /// </summary>
    [JsonPropertyName("maturity_ratings")]
    public List<string>? MaturityRatings { get; set; }

    /// <summary>
    /// Gets or sets the episode count.
    /// </summary>
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the season count.
    /// </summary>
    [JsonPropertyName("season_count")]
    public int SeasonCount { get; set; }

    /// <summary>
    /// Gets or sets the media count.
    /// </summary>
    [JsonPropertyName("media_count")]
    public int MediaCount { get; set; }

    /// <summary>
    /// Gets or sets the content provider.
    /// </summary>
    [JsonPropertyName("content_provider")]
    public string? ContentProvider { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is mature.
    /// </summary>
    [JsonPropertyName("is_mature")]
    public bool IsMature { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is simulcast.
    /// </summary>
    [JsonPropertyName("is_simulcast")]
    public bool IsSimulcast { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is dubbed.
    /// </summary>
    [JsonPropertyName("is_dubbed")]
    public bool IsDubbed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is subbed.
    /// </summary>
    [JsonPropertyName("is_subbed")]
    public bool IsSubbed { get; set; }

    /// <summary>
    /// Gets or sets the series launch year.
    /// </summary>
    [JsonPropertyName("series_launch_year")]
    public int? SeriesLaunchYear { get; set; }
}

/// <summary>
/// Represents a Crunchyroll season.
/// </summary>
public class CrunchyrollSeason
{
    /// <summary>
    /// Gets or sets the season ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the season title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the slug title.
    /// </summary>
    [JsonPropertyName("slug_title")]
    public string? SlugTitle { get; set; }

    /// <summary>
    /// Gets or sets the series ID this season belongs to.
    /// </summary>
    [JsonPropertyName("series_id")]
    public string? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the season sequence number.
    /// </summary>
    [JsonPropertyName("season_sequence_number")]
    public int SeasonSequenceNumber { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes.
    /// </summary>
    [JsonPropertyName("number_of_episodes")]
    public int NumberOfEpisodes { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is mature.
    /// </summary>
    [JsonPropertyName("is_mature")]
    public bool IsMature { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is subbed.
    /// </summary>
    [JsonPropertyName("is_subbed")]
    public bool IsSubbed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is dubbed.
    /// </summary>
    [JsonPropertyName("is_dubbed")]
    public bool IsDubbed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is complete.
    /// </summary>
    [JsonPropertyName("is_complete")]
    public bool IsComplete { get; set; }

    /// <summary>
    /// Gets or sets the keywords.
    /// </summary>
    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    /// <summary>
    /// Gets or sets the audio locales available.
    /// </summary>
    [JsonPropertyName("audio_locales")]
    public List<string>? AudioLocales { get; set; }

    /// <summary>
    /// Gets or sets the subtitle locales available.
    /// </summary>
    [JsonPropertyName("subtitle_locales")]
    public List<string>? SubtitleLocales { get; set; }

    /// <summary>
    /// Gets or sets the versions available.
    /// </summary>
    [JsonPropertyName("versions")]
    public List<CrunchyrollVersion>? Versions { get; set; }
}

/// <summary>
/// Represents a version of a season (different dubs/subs).
/// </summary>
public class CrunchyrollVersion
{
    /// <summary>
    /// Gets or sets the audio locale.
    /// </summary>
    [JsonPropertyName("audio_locale")]
    public string? AudioLocale { get; set; }

    /// <summary>
    /// Gets or sets the GUID.
    /// </summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's the original version.
    /// </summary>
    [JsonPropertyName("original")]
    public bool Original { get; set; }

    /// <summary>
    /// Gets or sets the variant.
    /// </summary>
    [JsonPropertyName("variant")]
    public string? Variant { get; set; }
}

/// <summary>
/// Represents a Crunchyroll episode.
/// </summary>
public class CrunchyrollEpisode
{
    /// <summary>
    /// Gets or sets the episode ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the series ID.
    /// </summary>
    [JsonPropertyName("series_id")]
    public string? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series title.
    /// </summary>
    [JsonPropertyName("series_title")]
    public string? SeriesTitle { get; set; }

    /// <summary>
    /// Gets or sets the series slug title.
    /// </summary>
    [JsonPropertyName("series_slug_title")]
    public string? SeriesSlugTitle { get; set; }

    /// <summary>
    /// Gets or sets the season ID.
    /// </summary>
    [JsonPropertyName("season_id")]
    public string? SeasonId { get; set; }

    /// <summary>
    /// Gets or sets the season title.
    /// </summary>
    [JsonPropertyName("season_title")]
    public string? SeasonTitle { get; set; }

    /// <summary>
    /// Gets or sets the season slug title.
    /// </summary>
    [JsonPropertyName("season_slug_title")]
    public string? SeasonSlugTitle { get; set; }

    /// <summary>
    /// Gets or sets the season number.
    /// </summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the slug title.
    /// </summary>
    [JsonPropertyName("slug_title")]
    public string? SlugTitle { get; set; }

    /// <summary>
    /// Gets or sets the episode number.
    /// </summary>
    [JsonPropertyName("episode")]
    public string? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the sequence number.
    /// </summary>
    [JsonPropertyName("sequence_number")]
    public float SequenceNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number as integer.
    /// </summary>
    [JsonPropertyName("episode_number")]
    public int? EpisodeNumberInt { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the next episode ID.
    /// </summary>
    [JsonPropertyName("next_episode_id")]
    public string? NextEpisodeId { get; set; }

    /// <summary>
    /// Gets or sets the next episode title.
    /// </summary>
    [JsonPropertyName("next_episode_title")]
    public string? NextEpisodeTitle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's a clip.
    /// </summary>
    [JsonPropertyName("is_clip")]
    public bool IsClip { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's mature.
    /// </summary>
    [JsonPropertyName("is_mature")]
    public bool IsMature { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's premium only.
    /// </summary>
    [JsonPropertyName("is_premium_only")]
    public bool IsPremiumOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's dubbed.
    /// </summary>
    [JsonPropertyName("is_dubbed")]
    public bool IsDubbed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it's subbed.
    /// </summary>
    [JsonPropertyName("is_subbed")]
    public bool IsSubbed { get; set; }

    /// <summary>
    /// Gets or sets the duration in milliseconds.
    /// </summary>
    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    /// <summary>
    /// Gets or sets the thumbnail.
    /// </summary>
    [JsonPropertyName("images")]
    public CrunchyrollEpisodeImages? Images { get; set; }

    /// <summary>
    /// Gets or sets the upload date.
    /// </summary>
    [JsonPropertyName("upload_date")]
    public string? UploadDate { get; set; }

    /// <summary>
    /// Gets or sets the episode air date.
    /// </summary>
    [JsonPropertyName("episode_air_date")]
    public string? EpisodeAirDate { get; set; }

    /// <summary>
    /// Gets or sets the premium available date.
    /// </summary>
    [JsonPropertyName("premium_available_date")]
    public string? PremiumAvailableDate { get; set; }

    /// <summary>
    /// Gets or sets the free available date.
    /// </summary>
    [JsonPropertyName("free_available_date")]
    public string? FreeAvailableDate { get; set; }

    /// <summary>
    /// Gets or sets HD flag.
    /// </summary>
    [JsonPropertyName("hd_flag")]
    public bool HdFlag { get; set; }

    /// <summary>
    /// Gets or sets the audio locale.
    /// </summary>
    [JsonPropertyName("audio_locale")]
    public string? AudioLocale { get; set; }

    /// <summary>
    /// Gets or sets the subtitle locales.
    /// </summary>
    [JsonPropertyName("subtitle_locales")]
    public List<string>? SubtitleLocales { get; set; }

    /// <summary>
    /// Gets or sets the maturity ratings.
    /// </summary>
    [JsonPropertyName("maturity_ratings")]
    public List<string>? MaturityRatings { get; set; }
}

/// <summary>
/// Represents Crunchyroll images.
/// </summary>
public class CrunchyrollImages
{
    /// <summary>
    /// Gets or sets the poster tall images.
    /// </summary>
    [JsonPropertyName("poster_tall")]
    public List<List<CrunchyrollImage>>? PosterTall { get; set; }

    /// <summary>
    /// Gets or sets the poster wide images.
    /// </summary>
    [JsonPropertyName("poster_wide")]
    public List<List<CrunchyrollImage>>? PosterWide { get; set; }
}

/// <summary>
/// Represents Crunchyroll episode images.
/// </summary>
public class CrunchyrollEpisodeImages
{
    /// <summary>
    /// Gets or sets the thumbnail images.
    /// </summary>
    [JsonPropertyName("thumbnail")]
    public List<List<CrunchyrollImage>>? Thumbnail { get; set; }
}

/// <summary>
/// Represents a single Crunchyroll image.
/// </summary>
public class CrunchyrollImage
{
    /// <summary>
    /// Gets or sets the image width.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the image height.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the image type.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the image source URL.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

/// <summary>
/// Search result item from Crunchyroll.
/// </summary>
public class CrunchyrollSearchResult
{
    /// <summary>
    /// Gets or sets the type of the result.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the count.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the items.
    /// </summary>
    [JsonPropertyName("items")]
    public List<CrunchyrollSearchItem>? Items { get; set; }
}

/// <summary>
/// Individual search item.
/// </summary>
public class CrunchyrollSearchItem
{
    /// <summary>
    /// Gets or sets the ID.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the slug title.
    /// </summary>
    [JsonPropertyName("slug_title")]
    public string? SlugTitle { get; set; }

    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the images.
    /// </summary>
    [JsonPropertyName("images")]
    public CrunchyrollImages? Images { get; set; }

    /// <summary>
    /// Gets or sets the series metadata.
    /// </summary>
    [JsonPropertyName("series_metadata")]
    public CrunchyrollSeriesMetadata? SeriesMetadata { get; set; }
}

/// <summary>
/// Series metadata in search results.
/// </summary>
public class CrunchyrollSeriesMetadata
{
    /// <summary>
    /// Gets or sets the episode count.
    /// </summary>
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets the season count.
    /// </summary>
    [JsonPropertyName("season_count")]
    public int SeasonCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is mature.
    /// </summary>
    [JsonPropertyName("is_mature")]
    public bool IsMature { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is dubbed.
    /// </summary>
    [JsonPropertyName("is_dubbed")]
    public bool IsDubbed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is subbed.
    /// </summary>
    [JsonPropertyName("is_subbed")]
    public bool IsSubbed { get; set; }

    /// <summary>
    /// Gets or sets the tenant categories.
    /// </summary>
    [JsonPropertyName("tenant_categories")]
    public List<string>? TenantCategories { get; set; }
}
