using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.BulkMetadata.Core;

/// <summary>
/// A serializable copy of an item's editable metadata. Scalars are stored as normalized strings,
/// lists as string lists. Provider ids live in <see cref="Scalars"/> under "ProviderIds.X" keys.
/// </summary>
public sealed class MetadataSnapshot
{
    /// <summary>Gets or sets scalar values.</summary>
    public Dictionary<string, string?> Scalars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets list values.</summary>
    public Dictionary<string, List<string>> Lists { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deep copy.</summary>
    /// <returns>Clone.</returns>
    public MetadataSnapshot Clone()
    {
        var c = new MetadataSnapshot();
        foreach (var kv in Scalars)
        {
            c.Scalars[kv.Key] = kv.Value;
        }

        foreach (var kv in Lists)
        {
            c.Lists[kv.Key] = new List<string>(kv.Value);
        }

        return c;
    }

    /// <summary>Gets a scalar, or null.</summary>
    /// <param name="key">Key.</param>
    /// <returns>Value.</returns>
    public string? Get(string key) => Scalars.TryGetValue(key, out var v) ? v : null;

    /// <summary>Gets a list (never null).</summary>
    /// <param name="key">Key.</param>
    /// <returns>Values.</returns>
    public List<string> GetList(string key)
    {
        if (!Lists.TryGetValue(key, out var l))
        {
            l = new List<string>();
            Lists[key] = l;
        }

        return l;
    }

    /// <summary>Gets a display string for any field.</summary>
    /// <param name="def">Field.</param>
    /// <returns>Display value.</returns>
    public string? Display(FieldDef def)
        => def.Kind == FieldKind.List ? (Lists.TryGetValue(def.Key, out var l) && l.Count > 0 ? string.Join(" | ", l) : null) : Get(def.Key);

    /// <summary>
    /// Returns the field keys whose value differs between two snapshots.
    /// </summary>
    /// <param name="before">Before.</param>
    /// <param name="after">After.</param>
    /// <returns>Changed keys.</returns>
    public static List<string> ChangedKeys(MetadataSnapshot before, MetadataSnapshot after)
    {
        var changed = new List<string>();
        foreach (var key in before.Scalars.Keys.Union(after.Scalars.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var a = before.Get(key);
            var b = after.Get(key);
            if (!string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }

        foreach (var key in before.Lists.Keys.Union(after.Lists.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var a = before.Lists.TryGetValue(key, out var la) ? la : new List<string>();
            var b = after.Lists.TryGetValue(key, out var lb) ? lb : new List<string>();
            if (!a.SequenceEqual(b, StringComparer.Ordinal))
            {
                changed.Add(key);
            }
        }

        return changed;
    }

    /// <summary>Returns a snapshot holding only the given keys.</summary>
    /// <param name="keys">Keys.</param>
    /// <returns>Subset.</returns>
    public MetadataSnapshot Subset(IEnumerable<string> keys)
    {
        var s = new MetadataSnapshot();
        foreach (var k in keys)
        {
            var def = FieldCatalog.Find(k);
            if (def is null)
            {
                continue;
            }

            if (def.Kind == FieldKind.List)
            {
                s.Lists[def.Key] = new List<string>(GetList(def.Key));
            }
            else
            {
                s.Scalars[def.Key] = Get(def.Key);
            }
        }

        return s;
    }

    /// <summary>All keys present.</summary>
    /// <returns>Keys.</returns>
    public IEnumerable<string> Keys() => Scalars.Keys.Concat(Lists.Keys);
}
