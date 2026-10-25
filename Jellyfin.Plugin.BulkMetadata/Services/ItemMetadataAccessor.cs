using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.BulkMetadata.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.BulkMetadata.Services;

/// <summary>
/// Reads and writes <see cref="MetadataSnapshot"/>s from/to Jellyfin items.
/// </summary>
public static class ItemMetadataAccessor
{
    /// <summary>Reads the editable metadata of an item.</summary>
    /// <param name="item">Item.</param>
    /// <returns>Snapshot.</returns>
    public static MetadataSnapshot Read(BaseItem item)
    {
        var s = new MetadataSnapshot();
        s.Scalars["Name"] = Clean(item.Name);
        s.Scalars["OriginalTitle"] = Clean(item.OriginalTitle);
        s.Scalars["ForcedSortName"] = Clean(item.ForcedSortName);
        s.Scalars["Overview"] = Clean(item.Overview);
        s.Scalars["Tagline"] = Clean(item.Tagline);
        s.Scalars["OfficialRating"] = Clean(item.OfficialRating);
        s.Scalars["CustomRating"] = Clean(item.CustomRating);
        s.Scalars["CommunityRating"] = item.CommunityRating.HasValue ? Math.Round(item.CommunityRating.Value, 2).ToString("0.##", CultureInfo.InvariantCulture) : null;
        s.Scalars["CriticRating"] = item.CriticRating.HasValue ? Math.Round(item.CriticRating.Value, 2).ToString("0.##", CultureInfo.InvariantCulture) : null;
        s.Scalars["ProductionYear"] = item.ProductionYear?.ToString(CultureInfo.InvariantCulture);
        s.Scalars["PremiereDate"] = item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        s.Scalars["EndDate"] = item.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        s.Scalars["PreferredMetadataLanguage"] = Clean(item.PreferredMetadataLanguage);
        s.Scalars["PreferredMetadataCountryCode"] = Clean(item.PreferredMetadataCountryCode);
        s.Scalars["IsLocked"] = item.IsLocked ? "true" : "false";

        s.Lists["Genres"] = (item.Genres ?? Array.Empty<string>()).ToList();
        s.Lists["Tags"] = (item.Tags ?? Array.Empty<string>()).ToList();
        s.Lists["Studios"] = (item.Studios ?? Array.Empty<string>()).ToList();
        s.Lists["ProductionLocations"] = (item.ProductionLocations ?? Array.Empty<string>()).ToList();
        s.Lists["LockedFields"] = (item.LockedFields ?? Array.Empty<MetadataField>()).Select(f => f.ToString()).ToList();

        if (item.ProviderIds is not null)
        {
            foreach (var kv in item.ProviderIds)
            {
                if (!string.IsNullOrWhiteSpace(kv.Value))
                {
                    s.Scalars[FieldCatalog.ProviderPrefix + kv.Key] = kv.Value;
                }
            }
        }

        return s;
    }

    /// <summary>Writes the given keys of <paramref name="s"/> into the item (does not save).</summary>
    /// <param name="item">Item.</param>
    /// <param name="s">Desired metadata.</param>
    /// <param name="keys">Keys to write.</param>
    public static void Write(BaseItem item, MetadataSnapshot s, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            var def = FieldCatalog.Find(key);
            if (def is null)
            {
                continue;
            }

            var v = s.Get(def.Key);
            switch (def.Key)
            {
                case "Name":
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        throw new FormatException("Title cannot be empty.");
                    }

                    item.Name = v;
                    break;
                case "OriginalTitle":
                    item.OriginalTitle = v;
                    break;
                case "ForcedSortName":
                    item.ForcedSortName = v;
                    break;
                case "Overview":
                    item.Overview = v;
                    break;
                case "Tagline":
                    item.Tagline = v;
                    break;
                case "OfficialRating":
                    item.OfficialRating = v;
                    break;
                case "CustomRating":
                    item.CustomRating = v;
                    break;
                case "CommunityRating":
                    item.CommunityRating = v is null ? null : float.Parse(v, CultureInfo.InvariantCulture);
                    break;
                case "CriticRating":
                    item.CriticRating = v is null ? null : float.Parse(v, CultureInfo.InvariantCulture);
                    break;
                case "ProductionYear":
                    item.ProductionYear = v is null ? null : int.Parse(v, CultureInfo.InvariantCulture);
                    break;
                case "PremiereDate":
                    item.PremiereDate = ParseDate(v);
                    break;
                case "EndDate":
                    item.EndDate = ParseDate(v);
                    break;
                case "PreferredMetadataLanguage":
                    item.PreferredMetadataLanguage = v;
                    break;
                case "PreferredMetadataCountryCode":
                    item.PreferredMetadataCountryCode = v;
                    break;
                case "IsLocked":
                    item.IsLocked = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "Genres":
                    item.Genres = s.GetList("Genres").ToArray();
                    break;
                case "Tags":
                    item.Tags = s.GetList("Tags").ToArray();
                    break;
                case "Studios":
                    item.Studios = s.GetList("Studios").ToArray();
                    break;
                case "ProductionLocations":
                    item.ProductionLocations = s.GetList("ProductionLocations").ToArray();
                    break;
                case "LockedFields":
                    item.LockedFields = s.GetList("LockedFields")
                        .Select(n => Enum.TryParse<MetadataField>(n, true, out var f) ? (MetadataField?)f : null)
                        .Where(f => f.HasValue)
                        .Select(f => f!.Value)
                        .Distinct()
                        .ToArray();
                    break;
                default:
                    if (def.Kind == FieldKind.ProviderId)
                    {
                        var provider = def.Key.Substring(FieldCatalog.ProviderPrefix.Length);
                        item.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var existing = item.ProviderIds.Keys.FirstOrDefault(k => string.Equals(k, provider, StringComparison.OrdinalIgnoreCase)) ?? provider;
                        if (string.IsNullOrWhiteSpace(v))
                        {
                            item.ProviderIds.Remove(existing);
                        }
                        else
                        {
                            item.ProviderIds[existing] = v;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Builds the {Token} context for an item.</summary>
    /// <param name="item">Item.</param>
    /// <param name="library">Library name.</param>
    /// <returns>Context.</returns>
    public static TokenContext BuildContext(BaseItem item, string? library)
    {
        var ctx = new TokenContext();
        ctx.Values["Type"] = item.GetBaseItemKind().ToString();
        ctx.Values["Library"] = library;
        if (item is IHasSeries hs)
        {
            ctx.Values["SeriesName"] = hs.SeriesName;
        }
        else if (item is Series)
        {
            ctx.Values["SeriesName"] = item.Name;
        }

        if (item is Episode)
        {
            ctx.Values["SeasonNumber"] = item.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture);
            ctx.Values["EpisodeNumber"] = item.IndexNumber?.ToString(CultureInfo.InvariantCulture);
        }
        else if (item is Season)
        {
            ctx.Values["SeasonNumber"] = item.IndexNumber?.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            ctx.Values["EpisodeNumber"] = item.IndexNumber?.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(item.Path))
        {
            try
            {
                ctx.Values["FileName"] = item.IsFolder ? Path.GetFileName(item.Path.TrimEnd('/', '\\')) : Path.GetFileNameWithoutExtension(item.Path);
                ctx.Values["Folder"] = Path.GetFileName(Path.GetDirectoryName(item.Path) ?? string.Empty);
            }
            catch (ArgumentException)
            {
                // Odd path; tokens stay empty.
            }
        }

        return ctx;
    }

    private static DateTime? ParseDate(string? v)
        => v is null ? null : DateTime.SpecifyKind(DateTime.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
