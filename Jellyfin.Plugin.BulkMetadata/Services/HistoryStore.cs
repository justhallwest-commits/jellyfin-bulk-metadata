using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.BulkMetadata.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkMetadata.Services;

/// <summary>One item changed by a batch, with enough data to undo it.</summary>
public sealed class HistoryItem
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the title at the time.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets values before the change (changed fields only).</summary>
    public MetadataSnapshot Before { get; set; } = new();

    /// <summary>Gets or sets values after the change (changed fields only).</summary>
    public MetadataSnapshot After { get; set; } = new();
}

/// <summary>Summary of a batch.</summary>
public class HistorySummary
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets when it ran.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets a description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of items changed.</summary>
    public int ItemCount { get; set; }

    /// <summary>Gets or sets the fields changed.</summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>Gets or sets the user who ran it.</summary>
    public string? User { get; set; }

    /// <summary>Gets or sets the id of the batch this one undid, if any.</summary>
    public string? UndoOf { get; set; }

    /// <summary>Gets or sets the id of the batch that undid this one, if any.</summary>
    public string? UndoneBy { get; set; }
}

/// <summary>A full batch record.</summary>
public sealed class HistoryEntry : HistorySummary
{
    /// <summary>Gets or sets the changed items.</summary>
    public List<HistoryItem> Items { get; set; } = new();
}

/// <summary>
/// Persists batch history as JSON in the plugin's data folder so batches can be undone.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly ILogger<HistoryStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="HistoryStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public HistoryStore(ILogger<HistoryStore> logger)
    {
        _logger = logger;
    }

    private static string Dir
    {
        get
        {
            var root = Plugin.Instance?.DataFolderPath ?? Path.Combine(Path.GetTempPath(), "jellyfin-bulk-metadata");
            var dir = Path.Combine(root, "history");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string IndexPath => Path.Combine(Dir, "index.json");

    /// <summary>Lists batches, newest first.</summary>
    /// <returns>Summaries.</returns>
    public List<HistorySummary> List()
    {
        lock (_lock)
        {
            return LoadIndex().OrderByDescending(e => e.TimestampUtc).ToList();
        }
    }

    /// <summary>Loads a batch.</summary>
    /// <param name="id">Id.</param>
    /// <returns>Entry or null.</returns>
    public HistoryEntry? Get(string id)
    {
        if (!IsSafeId(id))
        {
            return null;
        }

        lock (_lock)
        {
            var path = Path.Combine(Dir, id + ".json");
            if (!File.Exists(path))
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(path), _json);
            if (entry is not null)
            {
                // Index carries the latest UndoneBy link.
                var summary = LoadIndex().FirstOrDefault(s => s.Id == id);
                entry.UndoneBy = summary?.UndoneBy;
            }

            return entry;
        }
    }

    /// <summary>Saves a batch and trims old ones.</summary>
    /// <param name="entry">Entry.</param>
    public void Save(HistoryEntry entry)
    {
        lock (_lock)
        {
            File.WriteAllText(Path.Combine(Dir, entry.Id + ".json"), JsonSerializer.Serialize(entry, _json));
            var index = LoadIndex();
            index.RemoveAll(e => e.Id == entry.Id);
            index.Add(ToSummary(entry));
            if (entry.UndoOf is not null)
            {
                var orig = index.FirstOrDefault(e => e.Id == entry.UndoOf);
                if (orig is not null)
                {
                    orig.UndoneBy = entry.Id;
                }
            }

            var max = Math.Max(5, Plugin.Instance?.Configuration.MaxHistoryEntries ?? 50);
            foreach (var old in index.OrderByDescending(e => e.TimestampUtc).Skip(max).ToList())
            {
                index.Remove(old);
                TryDelete(Path.Combine(Dir, old.Id + ".json"));
            }

            SaveIndex(index);
        }
    }

    /// <summary>Deletes a batch record (does not change any metadata).</summary>
    /// <param name="id">Id.</param>
    /// <returns>Whether it existed.</returns>
    public bool Delete(string id)
    {
        if (!IsSafeId(id))
        {
            return false;
        }

        lock (_lock)
        {
            var index = LoadIndex();
            var removed = index.RemoveAll(e => e.Id == id) > 0;
            SaveIndex(index);
            TryDelete(Path.Combine(Dir, id + ".json"));
            return removed;
        }
    }

    private static HistorySummary ToSummary(HistoryEntry e) => new()
    {
        Id = e.Id,
        TimestampUtc = e.TimestampUtc,
        Description = e.Description,
        ItemCount = e.ItemCount,
        Fields = e.Fields,
        User = e.User,
        UndoOf = e.UndoOf,
        UndoneBy = e.UndoneBy,
    };

    private static bool IsSafeId(string id) => !string.IsNullOrEmpty(id) && id.All(char.IsLetterOrDigit);

    private List<HistorySummary> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                return JsonSerializer.Deserialize<List<HistorySummary>>(File.ReadAllText(IndexPath), _json) ?? new List<HistorySummary>();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Bulk metadata history index is unreadable; starting a new one");
        }

        return new List<HistorySummary>();
    }

    private static void SaveIndex(List<HistorySummary> index)
        => File.WriteAllText(IndexPath, JsonSerializer.Serialize(index, _json));

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }
}
