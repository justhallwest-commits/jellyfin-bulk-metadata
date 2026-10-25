using System;
using System.Collections.Generic;
using Jellyfin.Plugin.BulkMetadata.Core;

namespace Jellyfin.Plugin.BulkMetadata.Api;

/// <summary>Search / filter criteria.</summary>
public sealed class ItemFilter
{
    /// <summary>Gets or sets library (collection folder) ids; empty = all libraries.</summary>
    public List<Guid> LibraryIds { get; set; } = new();

    /// <summary>Gets or sets item types (Movie, Series, Season, Episode, ...); empty = common types.</summary>
    public List<string> ItemTypes { get; set; } = new();

    /// <summary>Gets or sets free text matched against title, original title, sort title and file path.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets text matched against the series name (episodes/seasons) or title (series).</summary>
    public string? SeriesText { get; set; }

    /// <summary>Gets or sets a field key to search inside (with <see cref="FieldQuery"/>).</summary>
    public string? FieldKey { get; set; }

    /// <summary>Gets or sets text that <see cref="FieldKey"/> must contain.</summary>
    public string? FieldQuery { get; set; }

    /// <summary>Gets or sets genres; item must have any of these.</summary>
    public List<string> HasGenres { get; set; } = new();

    /// <summary>Gets or sets tags; item must have any of these.</summary>
    public List<string> HasTags { get; set; } = new();

    /// <summary>Gets or sets tags the item must not have.</summary>
    public List<string> LacksTags { get; set; } = new();

    /// <summary>Gets or sets studios; item must have any of these.</summary>
    public List<string> HasStudios { get; set; } = new();

    /// <summary>Gets or sets parental ratings; item must have one of these ("(none)" matches empty).</summary>
    public List<string> OfficialRatings { get; set; } = new();

    /// <summary>Gets or sets the minimum year.</summary>
    public int? YearFrom { get; set; }

    /// <summary>Gets or sets the maximum year.</summary>
    public int? YearTo { get; set; }

    /// <summary>Gets or sets fields that must be empty (field keys, or PrimaryImage/BackdropImage/LogoImage/ThumbImage).</summary>
    public List<string> Missing { get; set; } = new();

    /// <summary>Gets or sets lock state: any, locked, unlocked, hasLockedFields.</summary>
    public string? Locked { get; set; }

    /// <summary>Gets or sets sort: Name, Year, DateAdded, Type, Library, Series, Rating.</summary>
    public string? SortBy { get; set; }

    /// <summary>Gets or sets a value indicating whether to sort descending.</summary>
    public bool SortDesc { get; set; }
}

/// <summary>Which items an action targets: explicit ids, or everything matching a filter.</summary>
public sealed class TargetSpec
{
    /// <summary>Gets or sets item ids. When non-empty these win over <see cref="Filter"/>.</summary>
    public List<Guid> ItemIds { get; set; } = new();

    /// <summary>Gets or sets the filter.</summary>
    public ItemFilter? Filter { get; set; }
}

/// <summary>Search request.</summary>
public sealed class SearchRequest
{
    /// <summary>Gets or sets the filter.</summary>
    public ItemFilter Filter { get; set; } = new();

    /// <summary>Gets or sets the page start.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    public int Limit { get; set; } = 50;

    /// <summary>Gets or sets a value indicating whether to return only ids (for "select all").</summary>
    public bool IdsOnly { get; set; }
}

/// <summary>Result row.</summary>
public sealed class ItemSummary
{
    /// <summary>Gets or sets id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets library name.</summary>
    public string? Library { get; set; }

    /// <summary>Gets or sets year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets series name.</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets season number.</summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets episode / track number.</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets parental rating.</summary>
    public string? OfficialRating { get; set; }

    /// <summary>Gets or sets community rating.</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets genres.</summary>
    public string[] Genres { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets tags.</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets studios.</summary>
    public string[] Studios { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a short overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets a value indicating whether the item has a primary image.</summary>
    public bool HasPrimaryImage { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is locked.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Gets or sets locked fields.</summary>
    public string[] LockedFields { get; set; } = Array.Empty<string>();
}

/// <summary>Search response.</summary>
public sealed class SearchResponse
{
    /// <summary>Gets or sets total matches.</summary>
    public int TotalRecordCount { get; set; }

    /// <summary>Gets or sets the page of items.</summary>
    public List<ItemSummary> Items { get; set; } = new();

    /// <summary>Gets or sets all ids (IdsOnly mode).</summary>
    public List<Guid>? Ids { get; set; }
}

/// <summary>Distinct values request.</summary>
public sealed class ValuesRequest
{
    /// <summary>Gets or sets which items to scan.</summary>
    public TargetSpec Target { get; set; } = new();

    /// <summary>Gets or sets the field (Genres, Tags, Studios, ProductionLocations, OfficialRating, CustomRating, ProductionYear, ...).</summary>
    public string Field { get; set; } = "Genres";
}

/// <summary>One distinct value.</summary>
public sealed class ValueCount
{
    /// <summary>Gets or sets the value.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of items with it.</summary>
    public int Count { get; set; }
}

/// <summary>Bulk edit request.</summary>
public sealed class EditRequest
{
    /// <summary>Gets or sets targets.</summary>
    public TargetSpec Target { get; set; } = new();

    /// <summary>Gets or sets operations, applied in order.</summary>
    public List<EditOperation> Operations { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether to lock every edited field so metadata refreshes don't overwrite it.</summary>
    public bool LockEditedFields { get; set; }

    /// <summary>Gets or sets an optional description for the history log.</summary>
    public string? Description { get; set; }
}

/// <summary>CSV import request.</summary>
public sealed class ImportRequest
{
    /// <summary>Gets or sets the CSV text.</summary>
    public string Csv { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether blank cells clear the field (otherwise blanks are ignored).</summary>
    public bool BlankMeansClear { get; set; }

    /// <summary>Gets or sets a value indicating whether to lock edited fields.</summary>
    public bool LockEditedFields { get; set; }
}

/// <summary>CSV export request.</summary>
public sealed class ExportRequest
{
    /// <summary>Gets or sets targets.</summary>
    public TargetSpec Target { get; set; } = new();

    /// <summary>Gets or sets columns; empty = defaults.</summary>
    public List<string> Columns { get; set; } = new();
}

/// <summary>Metadata refresh request.</summary>
public sealed class RefreshRequest
{
    /// <summary>Gets or sets targets.</summary>
    public TargetSpec Target { get; set; } = new();

    /// <summary>Gets or sets the mode: missing, metadata, images, all.</summary>
    public string Mode { get; set; } = "missing";
}

/// <summary>A single field change.</summary>
public sealed class FieldChange
{
    /// <summary>Gets or sets the field key.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the field label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the old value.</summary>
    public string? Before { get; set; }

    /// <summary>Gets or sets the new value.</summary>
    public string? After { get; set; }
}

/// <summary>Planned changes for one item.</summary>
public sealed class ItemChangePreview
{
    /// <summary>Gets or sets the item id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the library.</summary>
    public string? Library { get; set; }

    /// <summary>Gets or sets changes.</summary>
    public List<FieldChange> Changes { get; set; } = new();

    /// <summary>Gets or sets an error, if this item can't be changed.</summary>
    public string? Error { get; set; }
}

/// <summary>Preview response.</summary>
public sealed class PreviewResponse
{
    /// <summary>Gets or sets the number of items examined.</summary>
    public int TotalItems { get; set; }

    /// <summary>Gets or sets the number of items that would change.</summary>
    public int ChangedItems { get; set; }

    /// <summary>Gets or sets the number of field changes overall.</summary>
    public int TotalChanges { get; set; }

    /// <summary>Gets or sets the number of items with errors.</summary>
    public int ErrorItems { get; set; }

    /// <summary>Gets or sets per-field change counts.</summary>
    public Dictionary<string, int> ChangesByField { get; set; } = new();

    /// <summary>Gets or sets a sample of changed / errored items (capped).</summary>
    public List<ItemChangePreview> Items { get; set; } = new();

    /// <summary>Gets or sets warnings (e.g. CSV problems).</summary>
    public List<string> Warnings { get; set; } = new();
}
