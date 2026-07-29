// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Collections.Frozen;
using System.Text.Json;

namespace Neolink.WebClient.Localization;

/// <summary>
/// The translation catalogues, keyed by the ENGLISH source text.
///
/// Using the English sentence as the key (rather than an invented id like
/// "settings.general.title") is deliberate: the components stay readable, English
/// needs no catalogue at all, and a key with no translation yet renders as
/// English instead of as a missing-resource placeholder — so a half-finished
/// language is still a usable UI rather than a broken one.
///
/// Catalogues are embedded JSON (Localization/{code}.json), read once on first
/// use and then immutable, so lookups are a frozen-dictionary probe with no
/// locking on the render path.
/// </summary>
public static class Translations
{
    private static readonly Dictionary<string, FrozenDictionary<string, string>> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>The translated text, or <paramref name="text"/> itself when the
    /// language is English or the catalogue has no entry for it.</summary>
    public static string Get(string language, string text)
    {
        if (string.IsNullOrEmpty(text) || language.Equals(Lang.Default, StringComparison.OrdinalIgnoreCase))
            return text;
        return Catalogue(language).GetValueOrDefault(text) ?? text;
    }

    /// <summary>How many of a language's entries are filled in — surfaced by the
    /// picker so an incomplete language is visible before it is chosen.</summary>
    public static int Count(string language) =>
        language.Equals(Lang.Default, StringComparison.OrdinalIgnoreCase) ? 0 : Catalogue(language).Count;

    private static FrozenDictionary<string, string> Catalogue(string language)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(language, out var cached)) return cached;
            var map = Load(language);
            Loaded[language] = map;
            return map;
        }
    }

    private static FrozenDictionary<string, string> Load(string language)
    {
        var empty = FrozenDictionary<string, string>.Empty;
        try
        {
            var asm = typeof(Translations).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($".{language}.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return empty;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return empty;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (parsed == null) return empty;
            // Blank values are untranslated placeholders, not translations to "".
            return parsed.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .ToFrozenDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }
        catch
        {
            // A malformed catalogue must never take the UI down: English still works.
            return empty;
        }
    }
}
