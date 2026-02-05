using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Crunchyroll.ScheduledTasks;

public class ClearCrunchyrollIDsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ClearCrunchyrollIDsTask> _logger;

    public ClearCrunchyrollIDsTask(
        ILibraryManager libraryManager,
        ILogger<ClearCrunchyrollIDsTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Clear Crunchyroll IDs";
    public string Key => "Jellyfin.Plugin.CrunchyrollMetadata.ClearIDs";
    public string Description => "Clears Crunchyroll provider IDs on all Series/Seasons/Episodes in TV libraries to ensure Items will be remapped on the next metadata refresh.";
    public string Category => "Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => Array.Empty<TaskTriggerInfo>();

    //todo: doesnt work (throws error System.MissingMethodException: Method not found: 'System.Collections.Generic.List`1<MediaBrowser.Controller.Entities.BaseItem> MediaBrowser.Controller.Library.ILibraryManager.GetItemList(MediaBrowser.Controller.Entities.InternalItemsQuery)'.)
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Crunchyroll ID clearing task started.");
        var allTvItems = _libraryManager.GetItemsResult(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series,BaseItemKind.Season,BaseItemKind.Episode },
                    IsVirtualItem = false,
                    Recursive = true,
                }).Items;
        _logger.LogInformation("Found {Count} Items.", allTvItems.Count);

        var total = allTvItems.Count;
        var updated = 0;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = allTvItems[i];

            var key = item.ProviderIds?.Keys.FirstOrDefault(k => k.Contains("Crunchyroll", StringComparison.OrdinalIgnoreCase));

            if (null != key)
            {
                _logger.LogDebug("Clearing Crunchyroll ID for {ItemType}: {Name} (ID: {Id})",
                item.GetType().Name, item.Name, item.GetProviderId(key));

                item.ProviderIds.Remove(key);
                await SaveItemAsync(item, cancellationToken);
                updated++;
            }

            progress.Report(total == 0 ? 100 : (i + 1) * 100.0 / total);
        }

        _logger.LogInformation("Task finished. Updated {Updated} of {Total} items.", updated, total);
    }

    private Task SaveItemAsync(BaseItem item, CancellationToken ct)
    {
        item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
        return Task.CompletedTask;
    }
}
