// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
namespace Neolink.WebClient.Localization;

/// <summary>One language the UI can be displayed in.</summary>
/// <param name="Code">BCP-47 primary subtag, lowercase ("en", "fr").</param>
/// <param name="Native">What speakers of it call it — what the picker shows.</param>
/// <param name="English">The English name, for logs and English-language docs.</param>
public sealed record LangOption(string Code, string Native, string English);

/// <summary>
/// The languages the UI ships with. English is the source language: its strings
/// live in the components themselves and need no catalogue, so <see cref="All"/>
/// always starts with it and every other entry needs a catalogue in
/// <see cref="Translations"/>.
/// </summary>
public static class Lang
{
    public const string Default = "en";

    public static readonly IReadOnlyList<LangOption> All = new[]
    {
        new LangOption("en", "English", "English"),
        new LangOption("fr", "Français", "French"),
        new LangOption("de", "Deutsch", "German"),
        new LangOption("es", "Español", "Spanish"),
        new LangOption("nl", "Nederlands", "Dutch"),
        new LangOption("pl", "Polski", "Polish"),
        new LangOption("pt", "Português", "Portuguese"),
    };

    /// <summary>Region-tolerant, like <see cref="Normalize"/>: "fr-CA" is French.
    /// Returns false for anything we would have to fall back to English for, which
    /// is what lets a caller tell "no preference" from "a language we don't have".</summary>
    public static bool IsSupported(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var primary = code.Trim().Replace('_', '-').Split('-')[0];
        return All.Any(l => l.Code.Equals(primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps anything a browser, a config file or an old client might send to a
    /// language we actually have: case-insensitive, region-tolerant ("fr-CA" and
    /// "FR_ca" are both French), and unknown values fall back to English rather
    /// than leaving the UI blank.
    /// </summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return Default;
        var primary = code.Trim().Replace('_', '-').Split('-')[0];
        var match = All.FirstOrDefault(l => l.Code.Equals(primary, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? Default;
    }

    public static string NativeName(string code) =>
        All.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Native ?? code;
}
