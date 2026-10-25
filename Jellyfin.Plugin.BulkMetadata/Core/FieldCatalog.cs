using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.BulkMetadata.Core;

/// <summary>
/// The kind of value a metadata field holds.
/// </summary>
public enum FieldKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Whole number.</summary>
    Integer,

    /// <summary>Decimal number.</summary>
    Decimal,

    /// <summary>Calendar date (yyyy-MM-dd).</summary>
    Date,

    /// <summary>true / false.</summary>
    Boolean,

    /// <summary>List of strings.</summary>
    List,

    /// <summary>External provider id (e.g. ProviderIds.Imdb).</summary>
    ProviderId
}

/// <summary>
/// Describes one editable metadata field.
/// </summary>
/// <param name="Key">Stable key used by the API, CSV and UI.</param>
/// <param name="Label">Friendly label.</param>
/// <param name="Kind">Value kind.</param>
/// <param name="LockName">Name of the Jellyfin MetadataField lock that protects it, if any.</param>
public sealed record FieldDef(string Key, string Label, FieldKind Kind, string? LockName = null);

/// <summary>
/// Catalog of every field the plugin can read and write. Pure (no Jellyfin types) so it can be unit tested.
/// </summary>
public static class FieldCatalog
{
    /// <summary>Prefix used for provider id fields.</summary>
    public const string ProviderPrefix = "ProviderIds.";

    /// <summary>The fixed fields.</summary>
    public static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("Name", "Title", FieldKind.Text, "Name"),
        new("OriginalTitle", "Original title", FieldKind.Text),
        new("ForcedSortName", "Sort title", FieldKind.Text),
        new("Overview", "Overview / plot", FieldKind.Text, "Overview"),
        new("Tagline", "Tagline", FieldKind.Text),
        new("Genres", "Genres", FieldKind.List, "Genres"),
        new("Tags", "Tags", FieldKind.List, "Tags"),
        new("Studios", "Studios / networks", FieldKind.List, "Studios"),
        new("ProductionLocations", "Countries", FieldKind.List, "ProductionLocations"),
        new("OfficialRating", "Parental rating", FieldKind.Text, "OfficialRating"),
        new("CustomRating", "Custom rating", FieldKind.Text),
        new("CommunityRating", "Community rating", FieldKind.Decimal),
        new("CriticRating", "Critic rating", FieldKind.Decimal),
        new("ProductionYear", "Year", FieldKind.Integer),
        new("PremiereDate", "Release / premiere date", FieldKind.Date),
        new("EndDate", "End date", FieldKind.Date),
        new("PreferredMetadataLanguage", "Metadata language", FieldKind.Text),
        new("PreferredMetadataCountryCode", "Metadata country", FieldKind.Text),
        new("IsLocked", "Lock item (block all metadata refreshes)", FieldKind.Boolean),
        new("LockedFields", "Locked fields", FieldKind.List),
    };

    /// <summary>Common provider ids offered in the UI.</summary>
    public static readonly IReadOnlyList<string> CommonProviders = new[] { "Imdb", "Tmdb", "Tvdb", "TmdbCollection", "MusicBrainzAlbum", "MusicBrainzArtist", "AudioDbAlbum", "AudioDbArtist" };

    /// <summary>Lock names Jellyfin understands (MediaBrowser.Model.Entities.MetadataField).</summary>
    public static readonly IReadOnlyList<string> LockNames = new[] { "Name", "Overview", "Genres", "Tags", "Studios", "OfficialRating", "ProductionLocations", "Cast", "Runtime" };

    private static readonly Dictionary<string, FieldDef> _byKey = Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a field definition, including dynamic provider id fields.</summary>
    /// <param name="key">Field key.</param>
    /// <returns>The definition or null.</returns>
    public static FieldDef? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        key = key.Trim();
        if (_byKey.TryGetValue(key, out var def))
        {
            return def;
        }

        if (key.StartsWith(ProviderPrefix, StringComparison.OrdinalIgnoreCase) && key.Length > ProviderPrefix.Length)
        {
            var provider = key.Substring(ProviderPrefix.Length).Trim();
            var canonical = CommonProviders.FirstOrDefault(p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase)) ?? provider;
            return new FieldDef(ProviderPrefix + canonical, canonical + " id", FieldKind.ProviderId);
        }

        return null;
    }

    /// <summary>
    /// Validates and normalizes a scalar value for storage in a snapshot. Returns null for "empty".
    /// </summary>
    /// <param name="def">Field.</param>
    /// <param name="value">Raw value.</param>
    /// <returns>Normalized value.</returns>
    /// <exception cref="FormatException">Value is not valid for the field.</exception>
    public static string? NormalizeScalar(FieldDef def, string? value)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        switch (def.Kind)
        {
            case FieldKind.Integer:
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    return i.ToString(CultureInfo.InvariantCulture);
                }

                if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var di) && Math.Abs(di - Math.Round(di)) < 0.0001)
                {
                    return ((int)Math.Round(di)).ToString(CultureInfo.InvariantCulture);
                }

                throw new FormatException($"{def.Label} must be a whole number (got \"{value}\").");

            case FieldKind.Decimal:
                if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (d < 0 || d > 100)
                    {
                        throw new FormatException($"{def.Label} must be between 0 and 100 (got \"{value}\").");
                    }

                    return Math.Round(d, 2).ToString("0.##", CultureInfo.InvariantCulture);
                }

                throw new FormatException($"{def.Label} must be a number (got \"{value}\").");

            case FieldKind.Date:
                var formats = new[] { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffffffZ", "yyyy" };
                if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                    || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                {
                    return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                throw new FormatException($"{def.Label} must be a date like 2024-05-31 (got \"{value}\").");

            case FieldKind.Boolean:
                switch (value.ToLowerInvariant())
                {
                    case "true":
                    case "yes":
                    case "y":
                    case "1":
                    case "locked":
                    case "on":
                        return "true";
                    case "false":
                    case "no":
                    case "n":
                    case "0":
                    case "unlocked":
                    case "off":
                        return "false";
                    default:
                        throw new FormatException($"{def.Label} must be true or false (got \"{value}\").");
                }

            default:
                return value;
        }
    }

    /// <summary>Normalizes a list value: trims, drops blanks and case-insensitive duplicates.</summary>
    /// <param name="def">Field.</param>
    /// <param name="values">Values.</param>
    /// <returns>Normalized list.</returns>
    public static List<string> NormalizeList(FieldDef def, IEnumerable<string?> values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            if (raw is null)
            {
                continue;
            }

            var v = CollapseWhitespace(raw.Trim());
            if (v.Length == 0)
            {
                continue;
            }

            if (def.Key == "LockedFields")
            {
                var lockName = LockNames.FirstOrDefault(n => string.Equals(n, v, StringComparison.OrdinalIgnoreCase));
                if (lockName is null)
                {
                    // Allow passing a field key such as "ProductionLocations" or a label
                    var byField = Find(v);
                    lockName = byField?.LockName;
                }

                if (lockName is null)
                {
                    throw new FormatException($"\"{v}\" is not a lockable field. Lockable: {string.Join(", ", LockNames)}.");
                }

                v = lockName;
            }

            if (seen.Add(v))
            {
                result.Add(v);
            }
        }

        return result;
    }

    /// <summary>Splits user input into list items. Accepts |, ; and newlines (and commas when no other separator is present).</summary>
    /// <param name="input">Text.</param>
    /// <returns>Items.</returns>
    public static IEnumerable<string> SplitList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        char[] seps = input.IndexOfAny(new[] { '|', ';', '\n' }) >= 0 ? new[] { '|', ';', '\n', '\r' } : new[] { ',' };
        return input.Split(seps, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Collapses runs of whitespace.</summary>
    /// <param name="s">Text.</param>
    /// <returns>Collapsed text.</returns>
    public static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) && c != '\n')
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }

        return sb.ToString();
    }
}
