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
        var skipped = 0;
        int lastlog = 0;

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
            else
            {
                skipped++;
            }

            int progressint = (int)((i + 1) * 100.0 / total);
            if(progressint > lastlog)
            {
                _logger.LogInformation("Progress: {Progress}%. Updated {Updated} of {Total} items. Skipped {Skipped} items due to no Crunchyroll ID.", progressint, updated, total, skipped);
                lastlog = progressint;
            }

            progress.Report(total == 0 ? 100 : progressint);
        }

        _logger.LogInformation("Task finished. Updated {Updated} of {Total} items. Skipped {Skipped} items due to no Crunchyroll ID.", updated, total, skipped);
    }

    private async Task SaveItemAsync(BaseItem item, CancellationToken ct)
    {
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }
}
