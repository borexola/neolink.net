// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Globalization;
using System.Text;

namespace Neolink.Config;

/// <summary>
/// Minimal TOML reader covering the subset used by Neolink config files:
/// key = "string" | integer | boolean | [array], [table], [[array-of-tables]], # comments.
/// (Kept dependency-free so the app builds and runs offline.)
/// </summary>
public static class MiniToml
{
    public static Dictionary<string, object> Parse(string text)
    {
        var root = new Dictionary<string, object>();
        var current = root;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[") && line.EndsWith("]]"))
            {
                var name = line[2..^2].Trim();
                if (root.TryGetValue(name, out var existing) && existing is List<Dictionary<string, object>> list)
                {
                    current = new Dictionary<string, object>();
                    list.Add(current);
                }
                else
                {
                    var newList = new List<Dictionary<string, object>>();
                    current = new Dictionary<string, object>();
                    newList.Add(current);
                    root[name] = newList;
                }
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, object>();
                root[name] = current;
                continue;
            }

            int eq = IndexOfUnquoted(line, '=');
            if (eq < 0)
                throw new FormatException($"Invalid TOML line: {rawLine.Trim()}");
            var key = line[..eq].Trim().Trim('"', '\'');
            var valueText = line[(eq + 1)..].Trim();
            current[key] = ParseValue(valueText);
        }
        return root;
    }

    private static object ParseValue(string v)
    {
        if (v.Length == 0) throw new FormatException("Empty TOML value");
        if (v.StartsWith('"') || v.StartsWith('\''))
            return ParseString(v);
        if (v.StartsWith('['))
        {
            var inner = v[1..(v.EndsWith(']') ? ^1 : ^0)].Trim();
            var items = new List<object>();
            foreach (var part in SplitTopLevel(inner, ','))
            {
                var p = part.Trim();
                if (p.Length > 0) items.Add(ParseValue(p));
            }
            return items;
        }
        if (v == "true") return true;
        if (v == "false") return false;
        if (long.TryParse(v.Replace("_", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(v.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        // Bare value: treat as string (lenient)
        return v;
    }

    private static string ParseString(string v)
    {
        char quote = v[0];
        var sb = new StringBuilder();
        for (int i = 1; i < v.Length; i++)
        {
            char c = v[i];
            if (c == quote) break;
            if (quote == '"' && c == '\\' && i + 1 < v.Length)
            {
                i++;
                sb.Append(v[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '\\' => '\\',
                    '"' => '"',
                    var o => o,
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string StripComment(string line)
    {
        bool inString = false;
        char quote = ' ';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) inString = false;
            }
            else if (c == '"' || c == '\'')
            {
                inString = true;
                quote = c;
            }
            else if (c == '#')
            {
                return line[..i];
            }
        }
        return line;
    }

    private static int IndexOfUnquoted(string s, char target)
    {
        bool inString = false;
        char quote = ' ';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) inString = false;
            }
            else if (c == '"' || c == '\'') { inString = true; quote = c; }
            else if (c == target) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        int depth = 0;
        bool inString = false;
        char quote = ' ';
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) inString = false;
            }
            else if (c == '"' || c == '\'') { inString = true; quote = c; }
            else if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == sep && depth == 0)
            {
                yield return s[start..i];
                start = i + 1;
            }
        }
        if (start < s.Length) yield return s[start..];
    }

    // --------- typed accessors ---------

    public static string? GetString(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static long? GetInt(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) && v is long l ? l : null;

    public static double? GetDouble(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) ? v switch { double d => d, long l => l, _ => null } : null;

    public static bool? GetBool(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) && v is bool b
            ? b
            : bool.TryParse(GetString(table, key), out var parsed) ? parsed : null;

    /// <summary>A single [table] section, or null if absent.</summary>
    public static Dictionary<string, object>? GetTable(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) && v is Dictionary<string, object> d ? d : null;

    public static List<Dictionary<string, object>> GetTables(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) && v is List<Dictionary<string, object>> list
            ? list
            : new List<Dictionary<string, object>>();

    public static List<string>? GetStringList(Dictionary<string, object> table, string key) =>
        table.TryGetValue(key, out var v) && v is List<object> list
            ? list.Select(x => x.ToString() ?? "").ToList()
            : null;
}
