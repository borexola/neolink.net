// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Microsoft.JSInterop;

namespace Neolink.WebClient.Localization;

/// <summary>
/// The UI's language, scoped to one Blazor circuit — i.e. to one browser tab of
/// one signed-in user, which is exactly the granularity the setting has.
///
/// Components look strings up through the indexer and subscribe to
/// <see cref="OnChange"/>, so switching language re-renders the live UI in place:
/// no page reload, no reconnect, and no server restart. The chosen code is
/// mirrored into a cookie so the NEXT page load starts in the right language
/// instead of painting English first (the circuit is created before any of the
/// UI's own localStorage state has been read).
/// </summary>
public sealed class Translator
{
    /// <summary>Read server-side when a circuit starts; written by <see cref="SetAsync"/>.</summary>
    public const string CookieName = "neolink_lang";

    private readonly IJSRuntime _js;
    private readonly List<Func<Task>> _handlers = new();

    public Translator(IJSRuntime js) => _js = js;

    public string Language { get; private set; } = Lang.Default;

    public bool IsDefault => Language.Equals(Lang.Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>The translated text; the English original when untranslated.</summary>
    public string this[string text] => Translations.Get(Language, text);

    /// <summary>Translate then fill in the placeholders — the translated sentence
    /// decides where its values go, which is the whole point of not concatenating.</summary>
    public string F(string text, params object?[] args)
    {
        var format = Translations.Get(Language, text);
        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder must not throw mid-render:
            // fall back to the English source, which the build does check.
            return string.Format(text, args);
        }
    }

    // ---- dates in words -------------------------------------------------
    // The server builds with InvariantGlobalization (no ICU in the image), so
    // CultureInfo cannot supply French month/day names — they come from the
    // catalogue like every other string. Numeric formats (HH:mm, yyyy-MM-dd)
    // never needed a culture and are untouched.

    private static readonly string[] MonthNames =
    {
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    };
    private static readonly string[] MonthShortNames =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] DayNames =
        { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    private static readonly string[] DayShortNames =
        { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    public string Month(int month) => this[MonthNames[month - 1]];
    public string MonthShort(int month) => this[MonthShortNames[month - 1]];
    public string Day(DayOfWeek day) => this[DayNames[(int)day]];
    public string DayShort(DayOfWeek day) => this[DayShortNames[(int)day]];

    /// <summary>"Tue 29 Jul" — the compact date used on event rows and players.</summary>
    public string DateShort(DateTime d) => $"{DayShort(d.DayOfWeek)} {d.Day} {MonthShort(d.Month)}";

    /// <summary>"Tuesday, 29 July" — the spelled-out date used on day headers.</summary>
    public string DateLong(DateTime d) => $"{Day(d.DayOfWeek)}, {d.Day} {Month(d.Month)}";

    /// <summary>Sets the language without persisting it — how a circuit adopts the
    /// language the request already carried (cookie, account, server default).</summary>
    public void Initialize(string? code)
    {
        var normalized = Lang.Normalize(code);
        if (normalized == Language) return;
        Language = normalized;
    }

    /// <summary>Switches language and tells the browser about it: the cookie for
    /// the next load, and the html lang attribute for spell-checkers and screen
    /// readers, which read it live.</summary>
    public async Task SetAsync(string? code)
    {
        var normalized = Lang.Normalize(code);
        if (normalized == Language) return;
        Language = normalized;
        try
        {
            await _js.InvokeVoidAsync("neolink.setLang", normalized);
        }
        catch
        {
            // Prerender or a torn-down circuit: the in-memory switch still stands.
        }
        await NotifyAsync();
    }

    /// <summary>Subscribes to language changes; dispose to unsubscribe.</summary>
    public IDisposable OnChange(Func<Task> handler)
    {
        lock (_handlers) _handlers.Add(handler);
        return new Subscription(this, handler);
    }

    private async Task NotifyAsync()
    {
        Func<Task>[] handlers;
        lock (_handlers) handlers = _handlers.ToArray();
        foreach (var handler in handlers)
        {
            try
            {
                await handler();
            }
            catch
            {
                // One disposed component must not stop the rest from re-rendering.
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Translator _owner;
        private readonly Func<Task> _handler;
        public Subscription(Translator owner, Func<Task> handler) => (_owner, _handler) = (owner, handler);
        public void Dispose()
        {
            lock (_owner._handlers) _owner._handlers.Remove(_handler);
        }
    }
}
