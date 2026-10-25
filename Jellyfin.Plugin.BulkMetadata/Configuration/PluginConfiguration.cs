using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BulkMetadata.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets how many batches to keep for undo.</summary>
    public int MaxHistoryEntries { get; set; } = 50;

    /// <summary>Gets or sets a value indicating whether "Lock edited fields" starts ticked.</summary>
    public bool DefaultLockEditedFields { get; set; } = true;
}
