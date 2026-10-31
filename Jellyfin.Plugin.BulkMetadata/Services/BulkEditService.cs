using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.BulkMetadata.Api;
using Jellyfin.Plugin.BulkMetadata.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BulkMetadata.Services;

/// <summary>A resolved item plus the library it lives in.</summary>
/// <param name="Item">Item.</param>
/// <param name="Library">Library name.</param>
public sealed record TargetItem(BaseItem Item, string? Library);

/// <summary>Planned change to one item.</summary>
public sealed class ChangePlan
{
    /// <summary>Gets or sets the item.</summary>
    public required TargetItem Target { get; set; }

    /// <summary>Gets or sets current metadata.</summary>
    public required MetadataSnapshot Before { get; set; }

    /// <summary>Gets or sets desired metadata.</summary>
    public MetadataSnapshot? After { get; set; }

    /// <summary>Gets or sets the changed keys.</summary>
    public List<string> Changed { get; set; } = new();

    /// <summary>Gets or sets an error.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// The heart of the plugin: finding items, planning edits, previewing and applying them.
/// </summary>
public sealed class BulkEditService
{
    /// <summary>Item types offered by default (and used when none are selected).</summary>
    public static readonly BaseItemKind[] DefaultKinds =
    {
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode,
        BaseItemKind.BoxSet, BaseItemKind.MusicVideo, BaseItemKind.MusicAlbum, BaseItemKind.Audio,
        BaseItemKind.AudioBook, BaseItemKind.Book, BaseItemKind.Video, BaseItemKind.Trailer,
        BaseItemKind.Photo, BaseItemKind.PhotoAlbum
    };

    /// <summary>Columns exported to CSV by default.</summary>
    public static readonly string[] DefaultColumns =
    {
        "Id", "Library", "Type", "SeriesName", "Season", "Episode", "Name", "OriginalTitle", "ForcedSortName",
        "ProductionYear", "PremiereDate", "OfficialRating", "CustomRating", "CommunityRating", "CriticRating",
        "Genres", "Tags", "Studios", "ProductionLocations", "Tagline", "Overview",
        "ProviderIds.Imdb", "ProviderIds.Tmdb", "ProviderIds.Tvdb", "LockedFields", "IsLocked", "Path"
    };

    /// <summary>Read-only CSV columns (ignored on import).</summary>
    public static readonly HashSet<string> ReadOnlyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "Library", "Type", "SeriesName", "Season", "Episode", "Path"
    };

    private const int PreviewSampleSize = 300;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly JobManager _jobs;
    private readonly HistoryStore _history;
    private readonly ILogger<BulkEditService> _logger;

    /// <summary>Initializes a new instance of the <see cref="BulkEditService"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="providerManager">Provider manager.</param>
    /// <param name="fileSystem">File system.</param>
    /// <param name="jobs">Job manager.</param>
    /// <param name="history">History store.</param>
    /// <param name="logger">Logger.</param>
    public BulkEditService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        JobManager jobs,
        HistoryStore history,
        ILogger<BulkEditService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        ItemMetadataAccessor.PeopleReader = i => libraryManager.GetPeople(i);
        ItemMetadataAccessor.PeopleWriter = (i, people) => libraryManager.UpdatePeopleAsync(i, people, CancellationToken.None);
        _fileSystem = fileSystem;
        _jobs = jobs;
        _history = history;
        _logger = logger;
    }

    // ---------------------------------------------------------------- libraries & search

    /// <summary>Lists libraries.</summary>
    /// <returns>Id, name and type of each library.</returns>
    public List<object> GetLibraries()
        => _libraryManager.GetVirtualFolders()
            .Where(f => Guid.TryParse(f.ItemId, out _))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => (object)new { Id = Guid.Parse(f.ItemId), f.Name, CollectionType = f.CollectionType?.ToString() ?? "mixed" })
            .ToList();

    /// <summary>Resolves a target spec into items.</summary>
    /// <param name="target">Target.</param>
    /// <returns>Items.</returns>
    public List<TargetItem> ResolveTargets(TargetSpec target)
    {
        if (target.ItemIds is { Count: > 0 })
        {
            var list = new List<TargetItem>();
            foreach (var id in target.ItemIds.Distinct())
            {
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                string? lib = null;
                try
                {
                    lib = _libraryManager.GetCollectionFolders(item).Select(f => f.Name).FirstOrDefault();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not resolve library for {Id}", id);
                }

                list.Add(new TargetItem(item, lib));
            }

            return list;
        }

        return Search(target.Filter ?? new ItemFilter());
    }

    /// <summary>Finds every item matching a filter, sorted.</summary>
    /// <param name="filter">Filter.</param>
    /// <returns>Items.</returns>
    public List<TargetItem> Search(ItemFilter filter)
    {
        var kinds = ParseKinds(filter.ItemTypes);
        var seen = new HashSet<Guid>();
        var results = new List<TargetItem>();

        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(folder.ItemId, out var libId))
            {
                continue;
            }

            if (filter.LibraryIds.Count > 0 && !filter.LibraryIds.Contains(libId))
            {
                continue;
            }

            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = kinds,
                AncestorIds = new[] { libId },
            };

            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (seen.Add(item.Id) && Matches(item, filter))
                {
                    results.Add(new TargetItem(item, folder.Name));
                }
            }
        }

        return Sort(results, filter.SortBy, filter.SortDesc);
    }

    /// <summary>Runs a paged search.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Response.</returns>
    public SearchResponse SearchPage(SearchRequest request)
    {
        var all = Search(request.Filter ?? new ItemFilter());
        var resp = new SearchResponse { TotalRecordCount = all.Count };
        if (request.IdsOnly)
        {
            resp.Ids = all.Select(t => t.Item.Id).ToList();
            return resp;
        }

        var limit = Math.Clamp(request.Limit, 1, 500);
        resp.Items = all.Skip(Math.Max(0, request.StartIndex)).Take(limit).Select(ToSummary).ToList();
        return resp;
    }

    /// <summary>Counts distinct values of a field.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Values with counts, most used first.</returns>
    public List<ValueCount> GetValues(ValuesRequest request)
    {
        var def = FieldCatalog.Find(request.Field) ?? throw new ArgumentException($"Unknown field \"{request.Field}\".");
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ResolveTargets(request.Target))
        {
            var snap = ItemMetadataAccessor.Read(t.Item);
            IEnumerable<string> values = def.Kind == FieldKind.List
                ? snap.GetList(def.Key)
                : new[] { snap.Get(def.Key) ?? "(none)" };
            foreach (var v in values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[v] = counts.TryGetValue(v, out var c) ? c + 1 : 1;
                display.TryAdd(v, v);
            }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ValueCount { Value = display[kv.Key], Count = kv.Value })
            .ToList();
    }

    // ---------------------------------------------------------------- planning

    /// <summary>Plans an operation-based edit.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Plans.</returns>
    public List<ChangePlan> PlanEdit(EditRequest request)
    {
        OperationEngine.Validate(request.Operations);
        var plans = new List<ChangePlan>();
        foreach (var t in ResolveTargets(request.Target))
        {
            var before = ItemMetadataAccessor.Read(t.Item);
            var plan = new ChangePlan { Target = t, Before = before };
            try
            {
                var after = OperationEngine.Apply(before, request.Operations, ItemMetadataAccessor.BuildContext(t.Item, t.Library));
                Finish(plan, after, request.LockEditedFields);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                plan.Error = ex.Message;
            }

            plans.Add(plan);
        }

        return plans;
    }

    /// <summary>Plans a CSV import.</summary>
    /// <param name="request">Request.</param>
    /// <param name="warnings">Problems found.</param>
    /// <returns>Plans.</returns>
    public List<ChangePlan> PlanImport(ImportRequest request, List<string> warnings)
    {
        var rows = Csv.Parse(request.Csv ?? string.Empty);
        if (rows.Count < 2)
        {
            throw new ArgumentException("The CSV needs a header row and at least one data row.");
        }

        var header = rows[0].Select(h => h.Trim()).ToList();
        var idCol = header.FindIndex(h => string.Equals(h, "Id", StringComparison.OrdinalIgnoreCase));
        if (idCol < 0)
        {
            throw new ArgumentException("The CSV must have an \"Id\" column. Export from this plugin first, edit, then import.");
        }

        var columns = new List<(int Index, FieldDef Def)>();
        for (int i = 0; i < header.Count; i++)
        {
            if (i == idCol || ReadOnlyColumns.Contains(header[i]) || header[i].Length == 0)
            {
                continue;
            }

            var def = FieldCatalog.Find(header[i]) ?? FieldCatalog.Fields.FirstOrDefault(f => string.Equals(f.Label, header[i], StringComparison.OrdinalIgnoreCase));
            if (def is null)
            {
                warnings.Add($"Column \"{header[i]}\" isn't an editable field and was ignored.");
                continue;
            }

            columns.Add((i, def));
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("No editable columns were found in the CSV.");
        }

        var plans = new List<ChangePlan>();
        var seen = new HashSet<Guid>();
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var rawId = idCol < row.Count ? row[idCol].Trim() : string.Empty;
            if (!Guid.TryParse(rawId, out var id))
            {
                warnings.Add($"Row {r + 1}: \"{rawId}\" is not a valid item id; skipped.");
                continue;
            }

            if (!seen.Add(id))
            {
                warnings.Add($"Row {r + 1}: item {rawId} appears more than once; only the first row is used.");
                continue;
            }

            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                warnings.Add($"Row {r + 1}: item {rawId} no longer exists; skipped.");
                continue;
            }

            string? lib = null;
            try
            {
                lib = _libraryManager.GetCollectionFolders(item).Select(f => f.Name).FirstOrDefault();
            }
            catch (Exception)
            {
                // ignore
            }

            var before = ItemMetadataAccessor.Read(item);
            var plan = new ChangePlan { Target = new TargetItem(item, lib), Before = before };
            try
            {
                var after = before.Clone();
                foreach (var (index, def) in columns)
                {
                    var cell = index < row.Count ? row[index] : string.Empty;
                    if (string.IsNullOrWhiteSpace(cell) && !request.BlankMeansClear)
                    {
                        continue;
                    }

                    if (def.Kind == FieldKind.List)
                    {
                        after.Lists[def.Key] = FieldCatalog.NormalizeList(def, FieldCatalog.SplitList(cell.Replace("\r", string.Empty, StringComparison.Ordinal)));
                    }
                    else
                    {
                        after.Scalars[def.Key] = FieldCatalog.NormalizeScalar(def, cell);
                    }
                }

                Finish(plan, after, request.LockEditedFields);
            }
            catch (FormatException ex)
            {
                plan.Error = $"Row {r + 1}: {ex.Message}";
            }

            plans.Add(plan);
        }

        return plans;
    }

    /// <summary>Plans an undo of a history batch.</summary>
    /// <param name="entry">Batch.</param>
    /// <param name="warnings">Notes about items changed since.</param>
    /// <returns>Plans.</returns>
    public List<ChangePlan> PlanUndo(HistoryEntry entry, List<string> warnings)
    {
        var plans = new List<ChangePlan>();
        int drift = 0;
        foreach (var h in entry.Items)
        {
            var item = _libraryManager.GetItemById(h.ItemId);
            if (item is null)
            {
                warnings.Add($"\"{h.Name}\" no longer exists; skipped.");
                continue;
            }

            var current = ItemMetadataAccessor.Read(item);
            if (MetadataSnapshot.ChangedKeys(current.Subset(h.After.Keys()), h.After).Count > 0)
            {
                drift++;
            }

            var after = current.Clone();
            foreach (var kv in h.Before.Scalars)
            {
                after.Scalars[kv.Key] = kv.Value;
            }

            foreach (var kv in h.Before.Lists)
            {
                after.Lists[kv.Key] = new List<string>(kv.Value);
            }

            var plan = new ChangePlan { Target = new TargetItem(item, null), Before = current };
            Finish(plan, after, false);
            plans.Add(plan);
        }

        if (drift > 0)
        {
            warnings.Add($"{drift} item(s) were edited again after this batch; undo will restore the values from before the batch anyway.");
        }

        return plans;
    }

    private static void Finish(ChangePlan plan, MetadataSnapshot after, bool lockEdited)
    {
        var changed = MetadataSnapshot.ChangedKeys(plan.Before, after);
        if (lockEdited && changed.Count > 0)
        {
            var locks = after.GetList("LockedFields");
            var add = changed.Select(k => FieldCatalog.Find(k)?.LockName).Where(n => n is not null).Cast<string>();
            after.Lists["LockedFields"] = FieldCatalog.NormalizeList(FieldCatalog.Find("LockedFields")!, locks.Concat(add));
            changed = MetadataSnapshot.ChangedKeys(plan.Before, after);
        }

        plan.After = after;
        plan.Changed = changed;
    }

    // ---------------------------------------------------------------- preview & apply

    /// <summary>Builds a preview from plans.</summary>
    /// <param name="plans">Plans.</param>
    /// <param name="warnings">Warnings.</param>
    /// <returns>Preview.</returns>
    public static PreviewResponse Preview(List<ChangePlan> plans, List<string>? warnings = null)
    {
        var resp = new PreviewResponse { TotalItems = plans.Count, Warnings = warnings ?? new List<string>() };
        foreach (var p in plans)
        {
            if (p.Error is not null)
            {
                resp.ErrorItems++;
            }
            else if (p.Changed.Count > 0)
            {
                resp.ChangedItems++;
                resp.TotalChanges += p.Changed.Count;
                foreach (var k in p.Changed)
                {
                    var label = FieldCatalog.Find(k)?.Label ?? k;
                    resp.ChangesByField[label] = resp.ChangesByField.TryGetValue(label, out var c) ? c + 1 : 1;
                }
            }
            else
            {
                continue;
            }

            if (resp.Items.Count < PreviewSampleSize)
            {
                resp.Items.Add(new ItemChangePreview
                {
                    Id = p.Target.Item.Id,
                    Name = DisplayName(p.Target.Item),
                    Type = p.Target.Item.GetBaseItemKind().ToString(),
                    Library = p.Target.Library,
                    Error = p.Error,
                    Changes = p.Changed.Select(k =>
                    {
                        var def = FieldCatalog.Find(k)!;
                        return new FieldChange { Field = def.Key, Label = def.Label, Before = p.Before.Display(def), After = p.After!.Display(def) };
                    }).ToList(),
                });
            }
        }

        return resp;
    }

    /// <summary>Starts writing plans in the background.</summary>
    /// <param name="plans">Plans.</param>
    /// <param name="description">Description.</param>
    /// <param name="user">User name.</param>
    /// <param name="undoOf">History id being undone.</param>
    /// <returns>Job status.</returns>
    public JobStatus StartApply(List<ChangePlan> plans, string description, string? user, string? undoOf = null)
    {
        var work = plans.Where(p => p.Error is null && p.Changed.Count > 0).ToList();
        if (work.Count == 0)
        {
            throw new ArgumentException("Nothing to change — every targeted item already has these values.");
        }

        return _jobs.Start(description, work.Count, async (status, ct) =>
        {
            var entry = new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTime.UtcNow,
                Description = description,
                User = user,
                UndoOf = undoOf,
            };
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var plan in work)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    var item = plan.Target.Item;
                    try
                    {
                        // Re-fetch to work on the live instance.
                        item = _libraryManager.GetItemById(item.Id) ?? item;
                        ItemMetadataAccessor.Write(item, plan.After!, plan.Changed);
                        item.OnMetadataChanged();
                        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
                        await ItemMetadataAccessor.FlushPeopleAsync(item).ConfigureAwait(false);

                        entry.Items.Add(new HistoryItem
                        {
                            ItemId = item.Id,
                            Name = DisplayName(item),
                            Type = item.GetBaseItemKind().ToString(),
                            Before = plan.Before.Subset(plan.Changed),
                            After = plan.After!.Subset(plan.Changed),
                        });
                        foreach (var k in plan.Changed)
                        {
                            fields.Add(FieldCatalog.Find(k)?.Label ?? k);
                        }

                        status.Changed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Bulk metadata: failed to update {Name} ({Id})", item.Name, item.Id);
                        status.AddError($"{DisplayName(item)}: {ex.Message}");
                    }
                    finally
                    {
                        status.Processed++;
                    }
                }
            }
            finally
            {
                if (entry.Items.Count > 0)
                {
                    entry.ItemCount = entry.Items.Count;
                    entry.Fields = fields.OrderBy(f => f).ToList();
                    if (ct.IsCancellationRequested)
                    {
                        entry.Description += " (cancelled part-way)";
                    }

                    _history.Save(entry);
                    status.HistoryId = entry.Id;
                }

                _logger.LogInformation("Bulk metadata: \"{Description}\" changed {Count} item(s), {Errors} error(s)", description, status.Changed, status.ErrorCount);
            }
        });
    }

    // ---------------------------------------------------------------- export & refresh

    /// <summary>Exports items to CSV.</summary>
    /// <param name="request">Request.</param>
    /// <returns>CSV text.</returns>
    public string Export(ExportRequest request)
    {
        var cols = (request.Columns is { Count: > 0 } ? request.Columns : DefaultColumns.ToList())
            .Where(c => ReadOnlyColumns.Contains(c) || FieldCatalog.Find(c) is not null)
            .ToList();
        if (!cols.Contains("Id", StringComparer.OrdinalIgnoreCase))
        {
            cols.Insert(0, "Id");
        }

        var rows = new List<IEnumerable<string?>> { cols.Select(c => FieldCatalog.Find(c)?.Key ?? c).ToList() };
        foreach (var t in ResolveTargets(request.Target))
        {
            var snap = ItemMetadataAccessor.Read(t.Item);
            var item = t.Item;
            rows.Add(cols.Select(c =>
            {
                switch (c.ToLowerInvariant())
                {
                    case "id": return item.Id.ToString("N", CultureInfo.InvariantCulture);
                    case "library": return t.Library;
                    case "type": return item.GetBaseItemKind().ToString();
                    case "seriesname": return item is IHasSeries hs ? hs.SeriesName : null;
                    case "season": return (item is MediaBrowser.Controller.Entities.TV.Season ? item.IndexNumber : item.ParentIndexNumber)?.ToString(CultureInfo.InvariantCulture);
                    case "episode": return item is MediaBrowser.Controller.Entities.TV.Season ? null : item.IndexNumber?.ToString(CultureInfo.InvariantCulture);
                    case "path": return item.Path;
                }

                var def = FieldCatalog.Find(c)!;
                return def.Kind == FieldKind.List ? string.Join("|", snap.GetList(def.Key)) : snap.Get(def.Key);
            }).ToList());
        }

        return "﻿" + Csv.Write(rows);
    }

    /// <summary>Queues metadata refreshes.</summary>
    /// <param name="request">Request.</param>
    /// <returns>Number queued.</returns>
    public int Refresh(RefreshRequest request)
    {
        var mode = (request.Mode ?? "missing").ToLowerInvariant();
        int n = 0;
        foreach (var t in ResolveTargets(request.Target))
        {
            var opts = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = mode is "metadata" or "all",
                ReplaceAllImages = mode is "images" or "all",
                ForceSave = true,
                IsAutomated = false,
                RemoveOldMetadata = mode is "metadata" or "all",
            };
            _providerManager.QueueRefresh(t.Item.Id, opts, RefreshPriority.High);
            n++;
        }

        return n;
    }

    // ---------------------------------------------------------------- helpers

    private static BaseItemKind[] ParseKinds(List<string> types)
    {
        if (types is null || types.Count == 0)
        {
            return DefaultKinds;
        }

        var kinds = new List<BaseItemKind>();
        foreach (var t in types)
        {
            if (Enum.TryParse<BaseItemKind>(t, true, out var k))
            {
                kinds.Add(k);
            }
        }

        return kinds.Count > 0 ? kinds.Distinct().ToArray() : DefaultKinds;
    }

    private static bool ContainsAny(string[]? values, List<string> wanted)
        => values is not null && values.Any(v => wanted.Any(w => string.Equals(v, w, StringComparison.OrdinalIgnoreCase)));

    private static bool Contains(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(BaseItem item, ItemFilter f)
    {
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var text = f.Text.Trim();
            if (!(Contains(item.Name, text) || Contains(item.OriginalTitle, text) || Contains(item.ForcedSortName, text) || Contains(item.Path, text)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(f.SeriesText))
        {
            var series = item is IHasSeries hs ? hs.SeriesName : (item is MediaBrowser.Controller.Entities.TV.Series ? item.Name : null);
            if (!Contains(series, f.SeriesText.Trim()))
            {
                return false;
            }
        }

        if (f.HasGenres.Count > 0 && !ContainsAny(item.Genres, f.HasGenres))
        {
            return false;
        }

        if (f.HasTags.Count > 0 && !ContainsAny(item.Tags, f.HasTags))
        {
            return false;
        }

        if (f.LacksTags.Count > 0 && ContainsAny(item.Tags, f.LacksTags))
        {
            return false;
        }

        if (f.HasStudios.Count > 0 && !ContainsAny(item.Studios, f.HasStudios))
        {
            return false;
        }

        if (f.OfficialRatings.Count > 0)
        {
            var rating = string.IsNullOrWhiteSpace(item.OfficialRating) ? "(none)" : item.OfficialRating;
            if (!f.OfficialRatings.Any(r => string.Equals(r, rating, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (f.YearFrom.HasValue && (!item.ProductionYear.HasValue || item.ProductionYear < f.YearFrom))
        {
            return false;
        }

        if (f.YearTo.HasValue && (!item.ProductionYear.HasValue || item.ProductionYear > f.YearTo))
        {
            return false;
        }

        switch ((f.Locked ?? "any").ToLowerInvariant())
        {
            case "locked":
                if (!item.IsLocked)
                {
                    return false;
                }

                break;
            case "unlocked":
                if (item.IsLocked)
                {
                    return false;
                }

                break;
            case "haslockedfields":
                if (item.LockedFields is null || item.LockedFields.Length == 0)
                {
                    return false;
                }

                break;
        }

        MetadataSnapshot? snap = null;
        if (!string.IsNullOrWhiteSpace(f.FieldKey) && !string.IsNullOrWhiteSpace(f.FieldQuery))
        {
            var def = FieldCatalog.Find(f.FieldKey);
            if (def is not null)
            {
                snap ??= ItemMetadataAccessor.Read(item);
                if (!Contains(snap.Display(def), f.FieldQuery.Trim()))
                {
                    return false;
                }
            }
        }

        foreach (var missing in f.Missing)
        {
            switch (missing.ToLowerInvariant())
            {
                case "primaryimage":
                    if (item.HasImage(ImageType.Primary, 0))
                    {
                        return false;
                    }

                    continue;
                case "backdropimage":
                    if (item.HasImage(ImageType.Backdrop, 0))
                    {
                        return false;
                    }

                    continue;
                case "logoimage":
                    if (item.HasImage(ImageType.Logo, 0))
                    {
                        return false;
                    }

                    continue;
                case "thumbimage":
                    if (item.HasImage(ImageType.Thumb, 0))
                    {
                        return false;
                    }

                    continue;
            }

            var def = FieldCatalog.Find(missing);
            if (def is null)
            {
                continue;
            }

            snap ??= ItemMetadataAccessor.Read(item);
            if (!string.IsNullOrWhiteSpace(snap.Display(def)))
            {
                return false;
            }
        }

        return true;
    }

    private static List<TargetItem> Sort(List<TargetItem> items, string? sortBy, bool desc)
    {
        IOrderedEnumerable<TargetItem> ordered = (sortBy ?? "name").ToLowerInvariant() switch
        {
            "year" => items.OrderBy(t => t.Item.ProductionYear ?? 0),
            "dateadded" => items.OrderBy(t => t.Item.DateCreated),
            "type" => items.OrderBy(t => t.Item.GetBaseItemKind().ToString(), StringComparer.Ordinal),
            "library" => items.OrderBy(t => t.Library ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            "rating" => items.OrderBy(t => t.Item.CommunityRating ?? 0),
            "series" => items.OrderBy(t => (t.Item as IHasSeries)?.SeriesName ?? t.Item.SortName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Item.ParentIndexNumber ?? 0).ThenBy(t => t.Item.IndexNumber ?? 0),
            _ => items.OrderBy(t => t.Item.SortName ?? t.Item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase),
        };

        var list = ordered.ThenBy(t => t.Item.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
        if (desc)
        {
            list.Reverse();
        }

        return list;
    }

    private static string DisplayName(BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.TV.Episode ep)
        {
            var s = ep.ParentIndexNumber.HasValue ? $"S{ep.ParentIndexNumber:00}" : string.Empty;
            var e = ep.IndexNumber.HasValue ? $"E{ep.IndexNumber:00}" : string.Empty;
            return $"{ep.SeriesName} {s}{e} – {ep.Name}".Trim();
        }

        if (item is MediaBrowser.Controller.Entities.TV.Season season)
        {
            return $"{season.SeriesName} – {season.Name}";
        }

        return item.ProductionYear.HasValue ? $"{item.Name} ({item.ProductionYear})" : item.Name ?? string.Empty;
    }

    private static ItemSummary ToSummary(TargetItem t)
    {
        var item = t.Item;
        var overview = item.Overview;
        if (overview is { Length: > 220 })
        {
            overview = overview.Substring(0, 217) + "…";
        }

        return new ItemSummary
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            Type = item.GetBaseItemKind().ToString(),
            Library = t.Library,
            Year = item.ProductionYear,
            SeriesName = item is IHasSeries hs ? hs.SeriesName : null,
            ParentIndexNumber = item.ParentIndexNumber,
            IndexNumber = item.IndexNumber,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            Genres = item.Genres ?? Array.Empty<string>(),
            Tags = item.Tags ?? Array.Empty<string>(),
            Studios = item.Studios ?? Array.Empty<string>(),
            Overview = overview,
            HasPrimaryImage = item.HasImage(ImageType.Primary, 0),
            IsLocked = item.IsLocked,
            LockedFields = (item.LockedFields ?? Array.Empty<MetadataField>()).Select(x => x.ToString()).ToArray(),
        };
    }
}
