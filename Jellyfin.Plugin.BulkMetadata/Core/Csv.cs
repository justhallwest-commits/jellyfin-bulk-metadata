using System;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.BulkMetadata.Core;

/// <summary>
/// Minimal RFC 4180 CSV reader/writer (handles quotes, embedded commas and newlines, BOM, ; or tab delimiters).
/// </summary>
public static class Csv
{
    /// <summary>Escapes one cell.</summary>
    /// <param name="value">Value.</param>
    /// <returns>Escaped cell.</returns>
    public static string Escape(string? value)
    {
        value ??= string.Empty;

        // Guard against spreadsheet formula injection for cells that start with = + - @.
        if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '@') )
        {
            value = "'" + value;
        }

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r', ';', '\t' }) >= 0 || value.StartsWith(' ') || value.EndsWith(' '))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    /// <summary>Writes rows.</summary>
    /// <param name="rows">Rows.</param>
    /// <returns>CSV text.</returns>
    public static string Write(IEnumerable<IEnumerable<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', System.Linq.Enumerable.Select(row, Escape));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Parses CSV text into rows of cells.</summary>
    /// <param name="text">CSV text.</param>
    /// <returns>Rows.</returns>
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }

        if (text[0] == '﻿')
        {
            text = text.Substring(1);
        }

        char delim = DetectDelimiter(text);
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;
        bool cellWasQuoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            if (c == '"' && cell.Length == 0 && !cellWasQuoted)
            {
                inQuotes = true;
                cellWasQuoted = true;
            }
            else if (c == delim)
            {
                row.Add(Unguard(cell.ToString()));
                cell.Clear();
                cellWasQuoted = false;
            }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(Unguard(cell.ToString()));
                cell.Clear();
                cellWasQuoted = false;
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add(row);
                }

                row = new List<string>();
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0 || cellWasQuoted)
        {
            row.Add(Unguard(cell.ToString()));
            if (row.Count > 1 || row[0].Length > 0)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static string Unguard(string s)
        => s.Length > 1 && s[0] == '\'' && (s[1] == '=' || s[1] == '+' || s[1] == '@') ? s.Substring(1) : s;

    private static char DetectDelimiter(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        var header = end < 0 ? text : text.Substring(0, end);
        int commas = 0, semis = 0, tabs = 0;
        foreach (var c in header)
        {
            if (c == ',')
            {
                commas++;
            }
            else if (c == ';')
            {
                semis++;
            }
            else if (c == '\t')
            {
                tabs++;
            }
        }

        if (tabs > commas && tabs > semis)
        {
            return '\t';
        }

        return semis > commas ? ';' : ',';
    }
}
