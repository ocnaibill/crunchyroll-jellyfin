using Jellyfin.Plugin.Crunchyroll.Api;
using Jellyfin.Plugin.Crunchyroll.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Crunchyroll.Providers;

/// <summary>
/// Image provider for anime series from Crunchyroll.
/// </summary>
public class CrunchyrollSeriesImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly ILogger<CrunchyrollSeriesImageProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollSeriesImageProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CrunchyrollSeriesImageProvider(ILogger<CrunchyrollSeriesImageProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Crunchyroll";

    /// <inheritdoc />
    public int Order => 3;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        return item is Series;
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new[]
        {
            ImageType.Primary,
            ImageType.Backdrop,
            ImageType.Banner
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var images = new List<RemoteImageInfo>();

        string? crunchyrollId = item.GetProviderId("Crunchyroll");
        if (string.IsNullOrEmpty(crunchyrollId))
        {
            return images;
        }

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";

        using var httpClient = _httpClientFactory.CreateClient();
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale);

        var series = await apiClient.GetSeriesAsync(crunchyrollId, cancellationToken).ConfigureAwait(false);
        if (series?.Images == null)
        {
            return images;
        }

        // Add poster images (Primary)
        AddImages(images, series.Images.PosterTall, ImageType.Primary, "Poster");

        // Add backdrop images (wide posters as backdrops)
        AddImages(images, series.Images.PosterWide, ImageType.Backdrop, "Backdrop");

        // Use wide poster as banner too
        AddImages(images, series.Images.PosterWide, ImageType.Banner, "Banner");

        _logger.LogDebug("Found {Count} images for series {Name}", images.Count, item.Name);

        return images;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
    }

    private void AddImages(List<RemoteImageInfo> images, List<List<CrunchyrollImage>>? imageData, ImageType imageType, string prefix)
    {
        if (imageData == null || imageData.Count == 0)
        {
            return;
        }

        var imageSet = imageData.FirstOrDefault();
        if (imageSet == null)
        {
            return;
        }

        // Sort by resolution, highest first
        var sortedImages = imageSet
            .Where(i => !string.IsNullOrEmpty(i.Source))
            .OrderByDescending(i => i.Width * i.Height)
            .ToList();

        int index = 0;
        foreach (var img in sortedImages)
        {
            if (string.IsNullOrEmpty(img.Source))
            {
                continue;
            }

            images.Add(new RemoteImageInfo
            {
                Url = img.Source,
                Type = imageType,
                Width = img.Width,
                Height = img.Height,
                ProviderName = Name,
                Language = Plugin.Instance?.Configuration?.PreferredLanguage
            });

            index++;
        }
    }
}

/// <summary>
/// Image provider for anime episodes from Crunchyroll.
/// </summary>
public class CrunchyrollEpisodeImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly ILogger<CrunchyrollEpisodeImageProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrunchyrollEpisodeImageProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CrunchyrollEpisodeImageProvider(ILogger<CrunchyrollEpisodeImageProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Crunchyroll";

    /// <inheritdoc />
    public int Order => 3;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        return item is Episode;
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new[] { ImageType.Primary, ImageType.Thumb };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var images = new List<RemoteImageInfo>();

        string? crunchyrollEpisodeId = item.GetProviderId("CrunchyrollEpisode");
        if (string.IsNullOrEmpty(crunchyrollEpisodeId))
        {
            return images;
        }

        var config = Plugin.Instance?.Configuration;
        var locale = config?.PreferredLanguage ?? "pt-BR";

        using var httpClient = _httpClientFactory.CreateClient();
        using var apiClient = new CrunchyrollApiClient(httpClient, _logger, locale);

        var episode = await apiClient.GetEpisodeAsync(crunchyrollEpisodeId, cancellationToken).ConfigureAwait(false);
        if (episode?.Images?.Thumbnail == null)
        {
            return images;
        }

        var thumbnails = episode.Images.Thumbnail.FirstOrDefault();
        if (thumbnails == null)
        {
            return images;
        }

        foreach (var thumb in thumbnails.OrderByDescending(t => t.Width * t.Height))
        {
            if (string.IsNullOrEmpty(thumb.Source))
            {
                continue;
            }

            // Add as both Primary and Thumb
            images.Add(new RemoteImageInfo
            {
                Url = thumb.Source,
                Type = ImageType.Primary,
                Width = thumb.Width,
                Height = thumb.Height,
                ProviderName = Name
            });

            images.Add(new RemoteImageInfo
            {
                Url = thumb.Source,
                Type = ImageType.Thumb,
                Width = thumb.Width,
                Height = thumb.Height,
                ProviderName = Name
            });
        }

        return images;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return _httpClientFactory.CreateClient().GetAsync(new Uri(url), cancellationToken);
    }
}
