using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.BulkMetadata.Core;

/// <summary>
/// One edit to apply to every targeted item.
/// </summary>
public sealed class EditOperation
{
    /// <summary>
    /// Gets or sets the action: set, clear, append, prepend, add, remove, replace, case, trim, sort, copy,
    /// lockFields, unlockFields.
    /// </summary>
    public string Action { get; set; } = "set";

    /// <summary>Gets or sets the target field key.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the value (set/append/prepend; list input for add/remove/set on lists).</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets the text to find (replace).</summary>
    public string? Find { get; set; }

    /// <summary>Gets or sets the replacement (replace).</summary>
    public string? Replace { get; set; }

    /// <summary>Gets or sets a value indicating whether Find is a regular expression.</summary>
    public bool UseRegex { get; set; }

    /// <summary>Gets or sets a value indicating whether matching is case sensitive.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>Gets or sets a value indicating whether set should only fill empty fields.</summary>
    public bool OnlyIfEmpty { get; set; }

    /// <summary>Gets or sets the mode: for case = upper|lower|title|sentence.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the source field (copy).</summary>
    public string? SourceField { get; set; }
}

/// <summary>
/// Values available as {Tokens} inside set/append/prepend values.
/// </summary>
public sealed class TokenContext
{
    /// <summary>Gets or sets extra token values (SeriesName, SeasonNumber, EpisodeNumber, Type, Library, FileName, Folder).</summary>
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Applies <see cref="EditOperation"/>s to <see cref="MetadataSnapshot"/>s. Pure logic.
/// </summary>
public static class OperationEngine
{
    private static readonly Regex _tokenRegex = new(@"\{([A-Za-z][A-Za-z0-9\.]*)(?::(\w+))?\}", RegexOptions.Compiled);

    /// <summary>Validates operations before running a batch. Throws on the first problem.</summary>
    /// <param name="ops">Operations.</param>
    public static void Validate(IReadOnlyList<EditOperation> ops)
    {
        if (ops.Count == 0)
        {
            throw new ArgumentException("Add at least one edit.");
        }

        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var n = i + 1;
            var action = (op.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action is "lockfields" or "unlockfields")
            {
                if (FieldCatalog.SplitList(op.Value).Count() == 0)
                {
                    throw new ArgumentException($"Edit #{n}: choose at least one field to {(action == "lockfields" ? "lock" : "unlock")}.");
                }

                FieldCatalog.NormalizeList(FieldCatalog.Find("LockedFields")!, ExpandLockList(op.Value));
                continue;
            }

            var def = FieldCatalog.Find(op.Field) ?? throw new ArgumentException($"Edit #{n}: unknown field \"{op.Field}\".");
            switch (action)
            {
                case "set":
                case "clear":
                    break;
                case "append":
                case "prepend":
                    if (def.Kind is not (FieldKind.Text or FieldKind.List))
                    {
                        throw new ArgumentException($"Edit #{n}: {action} only works on text and list fields.");
                    }

                    break;
                case "add":
                case "remove":
                case "sort":
                    if (def.Kind != FieldKind.List)
                    {
                        throw new ArgumentException($"Edit #{n}: {action} only works on list fields (genres, tags, studios...).");
                    }

                    break;
                case "replace":
                    if (string.IsNullOrEmpty(op.Find))
                    {
                        throw new ArgumentException($"Edit #{n}: enter the text to find.");
                    }

                    if (op.UseRegex)
                    {
                        try
                        {
                            _ = new Regex(op.Find!, op.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                        }
                        catch (ArgumentException ex)
                        {
                            throw new ArgumentException($"Edit #{n}: invalid regular expression: {ex.Message}");
                        }
                    }

                    break;
                case "case":
                    if (def.Kind is not (FieldKind.Text or FieldKind.List))
                    {
                        throw new ArgumentException($"Edit #{n}: change case only works on text and list fields.");
                    }

                    break;
                case "trim":
                    break;
                case "copy":
                    if (FieldCatalog.Find(op.SourceField) is null)
                    {
                        throw new ArgumentException($"Edit #{n}: choose a field to copy from.");
                    }

                    break;
                default:
                    throw new ArgumentException($"Edit #{n}: unknown action \"{op.Action}\".");
            }
        }
    }

    /// <summary>
    /// Applies all operations to a copy of <paramref name="before"/>.
    /// </summary>
    /// <param name="before">Current metadata.</param>
    /// <param name="ops">Operations.</param>
    /// <param name="ctx">Token context.</param>
    /// <returns>New snapshot.</returns>
    public static MetadataSnapshot Apply(MetadataSnapshot before, IReadOnlyList<EditOperation> ops, TokenContext ctx)
    {
        var s = before.Clone();
        foreach (var op in ops)
        {
            ApplyOne(s, op, ctx);
        }

        return s;
    }

    /// <summary>Expands token placeholders such as {Name}, {Year}, {SeriesName}, {value}.</summary>
    /// <param name="template">Template.</param>
    /// <param name="s">Snapshot (current state).</param>
    /// <param name="ctx">Context.</param>
    /// <param name="current">Current value of the field being edited.</param>
    /// <returns>Expanded text.</returns>
    public static string ExpandTokens(string template, MetadataSnapshot s, TokenContext ctx, string? current)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0)
        {
            return template;
        }

        return _tokenRegex.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            string? val;
            if (string.Equals(name, "value", StringComparison.OrdinalIgnoreCase))
            {
                val = current;
            }
            else if (string.Equals(name, "Year", StringComparison.OrdinalIgnoreCase))
            {
                val = s.Get("ProductionYear");
            }
            else if (string.Equals(name, "Title", StringComparison.OrdinalIgnoreCase))
            {
                val = s.Get("Name");
            }
            else if (ctx.Values.TryGetValue(name, out var cv))
            {
                val = cv;
            }
            else
            {
                var def = FieldCatalog.Find(name);
                if (def is null)
                {
                    return m.Value; // leave unknown tokens untouched
                }

                val = s.Display(def);
            }

            val ??= string.Empty;
            if (format is not null && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) && format.All(c => c == '0'))
            {
                val = num.ToString(format, CultureInfo.InvariantCulture);
            }

            return val;
        });
    }

    private static IEnumerable<string> ExpandLockList(string? value)
    {
        var items = FieldCatalog.SplitList(value).ToList();
        if (items.Any(i => i == "*" || string.Equals(i, "all", StringComparison.OrdinalIgnoreCase)))
        {
            return FieldCatalog.LockNames;
        }

        return items;
    }

    private static void ApplyOne(MetadataSnapshot s, EditOperation op, TokenContext ctx)
    {
        var action = (op.Action ?? string.Empty).Trim().ToLowerInvariant();
        var lockDef = FieldCatalog.Find("LockedFields")!;

        if (action == "lockfields")
        {
            var current = s.GetList("LockedFields");
            s.Lists["LockedFields"] = FieldCatalog.NormalizeList(lockDef, current.Concat(ExpandLockList(op.Value)));
            return;
        }

        if (action == "unlockfields")
        {
            var remove = new HashSet<string>(FieldCatalog.NormalizeList(lockDef, ExpandLockList(op.Value)), StringComparer.OrdinalIgnoreCase);
            s.Lists["LockedFields"] = s.GetList("LockedFields").Where(v => !remove.Contains(v)).ToList();
            return;
        }

        var def = FieldCatalog.Find(op.Field)!;
        var cmp = op.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (def.Kind == FieldKind.List)
        {
            ApplyList(s, def, op, action, ctx, cmp);
            return;
        }

        var cur = s.Get(def.Key);
        string? next = cur;
        switch (action)
        {
            case "set":
                if (op.OnlyIfEmpty && !string.IsNullOrWhiteSpace(cur))
                {
                    return;
                }

                next = ExpandTokens(op.Value ?? string.Empty, s, ctx, cur);
                break;
            case "clear":
                next = null;
                break;
            case "append":
                next = (cur ?? string.Empty) + ExpandTokens(op.Value ?? string.Empty, s, ctx, cur);
                break;
            case "prepend":
                next = ExpandTokens(op.Value ?? string.Empty, s, ctx, cur) + (cur ?? string.Empty);
                break;
            case "replace":
                if (cur is null)
                {
                    return;
                }

                next = ReplaceText(cur, op, cmp, s, ctx);
                break;
            case "case":
                if (cur is null)
                {
                    return;
                }

                next = ChangeCase(cur, op.Mode);
                break;
            case "trim":
                next = cur is null ? null : FieldCatalog.CollapseWhitespace(cur.Trim());
                break;
            case "copy":
                var src = FieldCatalog.Find(op.SourceField)!;
                next = s.Display(src);
                if (op.OnlyIfEmpty && !string.IsNullOrWhiteSpace(cur))
                {
                    return;
                }

                break;
        }

        s.Scalars[def.Key] = FieldCatalog.NormalizeScalar(def, next);
    }

    private static void ApplyList(MetadataSnapshot s, FieldDef def, EditOperation op, string action, TokenContext ctx, StringComparison cmp)
    {
        var cur = s.GetList(def.Key);
        IEnumerable<string> ExpandEach(string? value) => FieldCatalog.SplitList(value).Select(v => ExpandTokens(v, s, ctx, null));

        List<string> next;
        switch (action)
        {
            case "set":
                if (op.OnlyIfEmpty && cur.Count > 0)
                {
                    return;
                }

                next = ExpandEach(op.Value).ToList();
                break;
            case "clear":
                next = new List<string>();
                break;
            case "add":
            case "append":
                next = cur.Concat(ExpandEach(op.Value)).ToList();
                break;
            case "prepend":
                next = ExpandEach(op.Value).Concat(cur).ToList();
                break;
            case "remove":
                var toRemove = ExpandEach(op.Value).ToList();
                if (op.UseRegex)
                {
                    var regexes = toRemove.Select(r => new Regex(r, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))).ToList();
                    next = cur.Where(v => !regexes.Any(r => r.IsMatch(v))).ToList();
                }
                else
                {
                    next = cur.Where(v => !toRemove.Any(r => string.Equals(r, v, StringComparison.OrdinalIgnoreCase))).ToList();
                }

                break;
            case "replace":
                // Each entry is matched as a whole value unless regex is used; a blank replacement removes it.
                next = new List<string>();
                foreach (var v in cur)
                {
                    string r;
                    if (op.UseRegex)
                    {
                        r = ReplaceText(v, op, cmp, s, ctx);
                    }
                    else
                    {
                        r = string.Equals(v, op.Find, cmp) ? ExpandTokens(op.Replace ?? string.Empty, s, ctx, v) : v;
                    }

                    next.Add(r);
                }

                break;
            case "case":
                next = cur.Select(v => ChangeCase(v, op.Mode)).ToList();
                break;
            case "trim":
                next = cur.ToList();
                break;
            case "sort":
                next = cur.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                break;
            case "copy":
                var src = FieldCatalog.Find(op.SourceField)!;
                if (op.OnlyIfEmpty && cur.Count > 0)
                {
                    return;
                }

                next = src.Kind == FieldKind.List ? s.GetList(src.Key).ToList() : FieldCatalog.SplitList(s.Get(src.Key)).ToList();
                break;
            default:
                return;
        }

        s.Lists[def.Key] = FieldCatalog.NormalizeList(def, next);
    }

    private static string ReplaceText(string input, EditOperation op, StringComparison cmp, MetadataSnapshot s, TokenContext ctx)
    {
        var replacement = op.Replace ?? string.Empty;
        if (op.UseRegex)
        {
            var re = new Regex(op.Find!, op.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            return re.Replace(input, ExpandTokens(replacement, s, ctx, input));
        }

        return input.Replace(op.Find!, ExpandTokens(replacement, s, ctx, input), cmp);
    }

    /// <summary>Changes the case of text.</summary>
    /// <param name="input">Text.</param>
    /// <param name="mode">upper, lower, title or sentence.</param>
    /// <returns>Converted text.</returns>
    public static string ChangeCase(string input, string? mode)
    {
        switch ((mode ?? "title").ToLowerInvariant())
        {
            case "upper":
                return input.ToUpperInvariant();
            case "lower":
                return input.ToLowerInvariant();
            case "sentence":
                var lower = input.ToLowerInvariant();
                var idx = lower.IndexOf(lower.FirstOrDefault(char.IsLetter));
                return idx < 0 ? lower : lower.Substring(0, idx) + char.ToUpperInvariant(lower[idx]) + lower.Substring(idx + 1);
            default:
                return TitleCase(input);
        }
    }

    private static readonly HashSet<string> _smallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "as", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "the", "to", "vs", "via", "with"
    };

    private static string TitleCase(string input)
    {
        var words = input.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0)
            {
                continue;
            }

            // Leave acronyms / mixed-case words (e.g. "NASA", "iPhone", "McQueen") alone.
            bool allUpper = w.Length > 1 && w.Where(char.IsLetter).All(char.IsUpper) && w.Any(char.IsLetter);
            bool mixed = w.Skip(1).Any(char.IsUpper) && w.Any(char.IsLower);
            if (allUpper || mixed)
            {
                continue;
            }

            var lw = w.ToLowerInvariant();
            if (i > 0 && i < words.Length - 1 && _smallWords.Contains(lw.Trim(':', ',', '.', '-')) && !words[i - 1].EndsWith(':'))
            {
                words[i] = lw;
                continue;
            }

            var first = lw.IndexOf(lw.FirstOrDefault(char.IsLetterOrDigit));
            words[i] = first < 0 ? lw : lw.Substring(0, first) + char.ToUpperInvariant(lw[first]) + lw.Substring(first + 1);
        }

        return string.Join(' ', words);
    }
}
