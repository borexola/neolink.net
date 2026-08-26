// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Neolink.Recording;

/// <summary>A parsed search: hard filters plus free-text keywords. Structured
/// means every word was consumed by the grammar — no AI needed.</summary>
public sealed class EventQuery
{
    public List<string> Labels { get; init; } = new();
    /// <summary>Negated labels ("no cars", "without people") — excluded, never matched.</summary>
    public List<string> NotLabels { get; init; } = new();
    public List<string> Cameras { get; init; } = new();
    public DateTime? FromLocal { get; set; }
    public DateTime? ToLocal { get; set; }
    public List<string> Keywords { get; init; } = new();
    /// <summary>A bare number the grammar could not place ("between 9 and 10") —
    /// worth an AI look even when no keywords remain.</summary>
    public bool StrayDigits { get; internal set; }
    /// <summary>The only date evidence was a bare clock time anchored to today —
    /// weak enough that an AI-supplied day should win the merge.</summary>
    internal bool TimeOnly { get; set; }

    /// <summary>The hard filters alone — what the AI judge pool is drawn from.</summary>
    public EventQuery StructuralOnly()
    {
        var q = new EventQuery { FromLocal = FromLocal, ToLocal = ToLocal };
        q.Labels.AddRange(Labels);
        q.NotLabels.AddRange(NotLabels);
        q.Cameras.AddRange(Cameras);
        return q;
    }
    public bool Structured => Keywords.Count == 0;
    public bool HasStructure =>
        Labels.Count > 0 || NotLabels.Count > 0 || Cameras.Count > 0 || FromLocal != null || ToLocal != null;
    internal bool IsEmpty => !HasStructure && Keywords.Count == 0;
}

/// <summary>Natural-language event search. Deterministic first: labels, camera
/// names and date phrases parse locally, leftover words match the stored AI
/// descriptions. Only a query with leftovers may involve the LLM — and then
/// solely to translate the phrase into this same structured plan; matching
/// itself never runs through a model.</summary>
public static class EventSearch
{
    private static readonly Dictionary<string, string[]> LabelWords = new()
    {
        ["person"] = new[] { "person", "people", "someone", "somebody", "anyone", "anybody", "everyone",
            "everybody", "human", "man", "woman", "lady", "boy", "girl", "guy", "kid", "child", "children",
            "male", "female" },
        ["vehicle"] = new[] { "vehicle", "car", "truck", "lorry", "van", "suv", "pickup", "sedan",
            "automobile", "motorcycle", "bike", "scooter" },
        ["animal"] = new[] { "animal", "dog", "cat", "pet", "bird", "raccoon", "deer", "fox", "coyote",
            "squirrel", "rabbit", "wildlife", "critter" },
        ["package"] = new[] { "package", "parcel", "delivery" },
        ["doorbell"] = new[] { "doorbell", "visitor", "rang" },
        ["motion"] = new[] { "motion", "movement" },
        ["crying"] = new[] { "crying", "cry" },
        ["line-crossing"] = new[] { "line-crossing", "crossline", "crossing", "tripwire" },
        ["intrusion"] = new[] { "intrusion", "intruder" },
        ["loitering"] = new[] { "loitering", "loiterer" },
    };

    // Words a query needs but a match never does — the asking, not the scene.
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "in", "on", "at", "the", "a", "an", "any", "all", "show", "me", "my", "of", "i", "we", "you",
        "from", "events", "event", "clips", "clip", "was", "were", "there", "did", "is", "are", "be",
        "been", "being", "it", "its", "do", "does", "doing", "this", "that", "these", "those", "to",
        "by", "up", "out", "off", "into", "near", "over", "under", "about", "around", "how", "who",
        "where", "why", "which", "when", "what", "get", "got", "see", "saw", "went", "go", "going",
        "come", "came", "coming", "pass", "passed", "passing", "happen", "happened", "happens",
        "anything", "something", "everything", "nothing", "stuff", "activity", "latest", "newest",
        "recent", "recently", "first", "last", "past", "please", "can", "could", "would", "should",
        "like", "want", "wanted", "need", "look", "looking", "check", "view", "watch", "list", "give",
        "tell", "again", "still", "just", "ever", "then", "than", "if", "but", "so", "as", "has",
        "have", "had", "with", "and", "or", "find", "search", "for", "camera", "cam", "cameras",
        "detected", "detect", "detection", "detections", "seen", "spotted", "captured", "recorded",
        "no", "not", "without", "between", "one", "ones", "footage", "video", "videos",
        "since", "until", "till", "before", "after", "ago", "except", "excluding",
        "oclock", "o'clock", "during",
        "wearing", "wears", "wore", "dressed",
    };

    // Threat-level vocabulary: "suspicious" should surface yellow events even
    // when the description never uses the word.
    private static readonly Dictionary<string, string> LevelWords = new(StringComparer.Ordinal)
    {
        ["suspicious"] = "yellow", ["weird"] = "yellow", ["strange"] = "yellow", ["unusual"] = "yellow",
        ["odd"] = "yellow", ["sketchy"] = "yellow",
        ["danger"] = "red", ["dangerous"] = "red", ["threat"] = "red", ["alarming"] = "red",
        ["scary"] = "red", ["emergency"] = "red",
    };

    // Constrained month spellings: "may" must be followed by punctuation/space, so
    // "maybe 2" can never read as May 2nd, and "junk 3" never as June 3rd.
    private const string MonthsAlt =
        "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?" +
        "|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
    private const string WeekdaysAlt = "monday|tuesday|wednesday|thursday|friday|saturday|sunday";

    private static readonly string[] MonthNames =
        { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
    private static readonly string[] DayNames =
        { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    private static HashSet<string>? _englishVocab;
    internal static HashSet<string> EnglishVocab => _englishVocab ??= BuildEnglishVocab();

    private static HashSet<string> BuildEnglishVocab()
    {
        var v = new HashSet<string>(Filler, StringComparer.Ordinal);
        foreach (var kv in LabelWords)
        {
            v.Add(kv.Key);
            foreach (var w in kv.Value) { v.Add(w); v.Add(w + "s"); }
        }
        v.UnionWith(DayNames);
        v.UnionWith(MonthNames);
        v.Add("sept");
        v.UnionWith(LevelWords.Keys);
        v.UnionWith(new[]
        {
            "today", "yesterday", "tonight", "week", "weeks", "month", "months", "day", "days",
            "hour", "hours", "minute", "minutes", "weekend", "morning", "afternoon", "evening",
            "night", "noon", "midnight", "january", "february", "march", "april", "may", "june",
            "july", "august", "september", "october", "november", "december",
        });
        return v;
    }

    public static EventQuery Parse(string query, IReadOnlyList<string> cameraNames, DateTime nowLocal)
    {
        var q = new EventQuery();
        string s = " " + query.ToLowerInvariant() + " ";
        // Cameras first, longest name first, so "Front Door 2" wins over "Front
        // Door". Each name also matches its spaced camel-case split ("TestCam" ↔
        // "test cam") and its squashed form ("Front Door" ↔ "frontdoor"); every
        // occurrence is tried, so "driveways or driveway" still binds.
        foreach (var name in cameraNames.OrderByDescending(n => n.Length))
        {
            bool bound = false;
            foreach (var variant in CameraVariants(name))
            {
                int idx = 0;
                while (!bound && (idx = s.IndexOf(variant, idx + 1, StringComparison.Ordinal)) > 0)
                {
                    if (!char.IsLetterOrDigit(s[idx - 1]) && !char.IsLetterOrDigit(s[idx + variant.Length]))
                    {
                        if (!q.Cameras.Contains(name)) q.Cameras.Add(name);
                        s = s.Remove(idx, variant.Length).Insert(idx, " ");
                        bound = true;
                    }
                }
                if (bound) break;
            }
        }
        // After camera binding so native camera names stay matchable.
        s = SearchLingo.Normalize(s, out bool dayFirst);
        s = Regex.Replace(s, @"\bline[\s-]?crossing\b", " line-crossing ");
        // Negations before label extraction: "no cars today" must EXCLUDE
        // vehicles, never filter to them — and lists distribute ("no cars or
        // people" negates both).
        s = Regex.Replace(s,
            @"\b(?:no|not|without|except(?:\s+for)?|excluding)\s+((?:(?:a|an|any|the)\s+)?[a-z-]+(?:\s+(?:or|and)\s+(?:(?:a|an|any|the)\s+)?[a-z-]+)*)",
            m =>
            {
                var items = Regex.Split(m.Groups[1].Value, @"\s+(?:or|and)\s+");
                int consumed = 0;
                foreach (var item in items)
                {
                    var label = LabelOf(Regex.Replace(item, @"^(?:a|an|any|the)\s+", ""));
                    if (label == null) break;
                    if (!q.NotLabels.Contains(label)) q.NotLabels.Add(label);
                    consumed++;
                }
                if (consumed == 0) return m.Value;
                return " " + string.Join(" or ", items.Skip(consumed)) + " ";
            });
        s = ParseDates(s, q, nowLocal, dayFirst);
        foreach (var tok in Regex.Split(s, @"[^a-z0-9'\-]+"))
        {
            if (tok.Length > 0 && tok.All(char.IsDigit))
            {
                q.StrayDigits = true; // a number the grammar didn't consume — let the AI look
                continue;
            }
            if (tok.Length < 3 || Filler.Contains(tok)) continue;
            var label = LabelOf(tok);
            if (label != null)
            {
                if (!q.Labels.Contains(label) && !q.NotLabels.Contains(label)) q.Labels.Add(label);
                continue;
            }
            if (!q.Keywords.Contains(tok)) q.Keywords.Add(tok);
        }
        return q;
    }

    /// <summary>Canonical label for a token, tolerating plurals (dogs→dog,
    /// deliveries→delivery); null when it is no label word.</summary>
    private static string? LabelOf(string tok)
    {
        if (LabelWords.ContainsKey(tok)) return tok;
        string? Hit(string t) => LabelWords.FirstOrDefault(kv => kv.Value.Contains(t)).Key;
        var hit = Hit(tok);
        if (hit == null && tok.EndsWith("ies")) hit = Hit(tok[..^3] + "y");
        if (hit == null && tok.EndsWith("es")) hit = Hit(tok[..^2]);
        if (hit == null && tok.EndsWith("s")) hit = Hit(tok[..^1]);
        return hit;
    }

    private static string ParseDates(string s, EventQuery q, DateTime now, bool dayFirst = false)
    {
        var today = now.Date;
        var days = new List<DateTime>();
        bool explicitDate = false;
        DateTime? rFrom = null, rTo = null;   // whole ranges (last week, last night, …)
        DateTime? qFrom = null, qTo = null;   // one-sided bounds (since/before …)
        int? tFrom = null, tTo = null;        // clock times, minutes into the day
        int? todFrom = null, todTo = null;    // time-of-day words (morning, night, …)

        void Take(string pattern, Func<Match, bool> on)
        {
            var m = Regex.Match(s, pattern);
            if (!m.Success || !on(m)) return;
            s = s.Remove(m.Index, m.Length).Insert(m.Index, " ");
        }
        void TakeAll(string pattern, Action<Match> on) => Take(pattern, m => { on(m); return true; });
        DateTime Monday() => today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateTime? DayWord(string w)
        {
            if (w == "today") return today;
            if (w == "yesterday") return today.AddDays(-1);
            int wd = Array.IndexOf(DayNames, w);
            return wd < 0 ? null : today.AddDays(-(((int)today.DayOfWeek - wd + 7) % 7));
        }

        // "since last month/weekend/night": the phrase's start becomes an open bound.
        bool sinceRange = false;
        bool bareHour = false;
        string? todWord = null;
        TakeAll(@"\b(?:since|after)\s+(?=last\s|this\s|(?:the\s+)?weekend)", _ => sinceRange = true);
        // Fixed multi-word phrases first — longer patterns must not be eaten
        // piecemeal by the shorter ones below.
        TakeAll(@"\b(?:the\s+)?day before yesterday\b", _ => days.Add(today.AddDays(-2)));
        TakeAll(@"\blast night\b", _ => { rFrom = today.AddDays(-1).AddHours(20); rTo = today.AddHours(6); });
        // Before 06:00 "tonight" means the night still in progress — yesterday's evening.
        TakeAll(@"\btonight\b", _ =>
        {
            var b = now.Hour < 6 ? today.AddDays(-1) : today;
            rFrom = b.AddHours(18);
            rTo = b.AddDays(1).AddHours(6);
        });
        TakeAll(@"\bthis morning\b", _ => { rFrom = today.AddHours(6); rTo = today.AddHours(12); });
        TakeAll(@"\bthis afternoon\b", _ => { rFrom = today.AddHours(12); rTo = today.AddHours(18); });
        TakeAll(@"\bthis evening\b", _ => { rFrom = today.AddHours(18); rTo = today.AddDays(1); });
        TakeAll(@"\b(?:(last|this)\s+|over\s+the\s+)?weekend\b", m =>
        {
            var sat = today.AddDays(-(((int)today.DayOfWeek - 6 + 7) % 7));
            if (m.Groups[1].Value == "last"
                && today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) sat = sat.AddDays(-7);
            rFrom = sat;
            rTo = sat.AddDays(2);
        });
        TakeAll(@"\b(since|after)\s+last\s+week\b", _ => qFrom = Monday().AddDays(-7));
        TakeAll(@"\blast week\b", _ => { rFrom = Monday().AddDays(-7); rTo = Monday(); });
        TakeAll(@"\bthis week\b", _ => { rFrom = Monday(); rTo = today.AddDays(1); });
        TakeAll(@"\blast month\b", _ =>
        {
            var first = new DateTime(today.Year, today.Month, 1);
            rFrom = first.AddMonths(-1);
            rTo = first;
        });
        TakeAll(@"\bthis month\b", _ => { rFrom = new DateTime(today.Year, today.Month, 1); rTo = today.AddDays(1); });
        TakeAll(@"\b(?:last|past)\s+(\d{1,3})\s+(minute|hour|day|week|month)s?\b", m =>
        {
            int n = int.Parse(m.Groups[1].Value);
            rFrom = Back(now, today, n, m.Groups[2].Value);
            rTo = now.AddMinutes(1);
        });
        TakeAll(@"\b(?:last|past)\s+(?:few|couple(?:\s+of)?)\s+(minute|hour|day|week)s?\b", m =>
        {
            rFrom = Back(now, today, 3, m.Groups[1].Value);
            rTo = now.AddMinutes(1);
        });
        TakeAll(@"\b(?:last|past)\s+(hour|day|week|month)\b", m =>
        {
            rFrom = Back(now, today, 1, m.Groups[1].Value);
            rTo = now.AddMinutes(1);
        });
        TakeAll(@"\b(an?|one|two|three|four|five|six|seven|eight|nine|ten|couple(?:\s+of)?|few|\d{1,3})\s+(minute|hour|day|week|month)s?\s+ago\b", m =>
        {
            int n = WordNum(m.Groups[1].Value);
            var unit = m.Groups[2].Value;
            if (unit is "minute" or "hour")
            {
                rFrom = Back(now, today, n, unit);
                rTo = now.AddMinutes(1);
            }
            else
            {
                // "2 days ago" means THAT day, not a rolling window.
                days.Add(unit switch
                {
                    "week" => today.AddDays(-7 * n),
                    "month" => today.AddMonths(-n),
                    _ => today.AddDays(-n),
                });
            }
        });
        TakeAll($@"\b(since|after)\s+(?:the\s+)?({WeekdaysAlt}|today|yesterday)\b",
            m => qFrom = DayWord(m.Groups[2].Value));
        // No "by" here: "did anyone come BY yesterday" is phrasal, not a bound.
        TakeAll($@"\b(before|until|till)\s+(?:the\s+)?({WeekdaysAlt}|today|yesterday)\b",
            m => qTo = DayWord(m.Groups[2].Value));
        // Explicit dates may come in pairs ("from 8/20 to 8/22") — the days list
        // spans them, so each pattern gets two bites.
        for (int i = 0; i < 2; i++)
        {
            bool got = false;
            TakeAll(@"\b(20\d\d)-(\d\d)-(\d\d)\b", m =>
            {
                got = true;
                var d = SafeDate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
                if (d != null) { days.Add(d.Value); explicitDate = true; }
            });
            if (!got) break;
        }
        // US-style 8/20, 8/20/2026 — month/day, day/month tried when month > 12.
        for (int i = 0; i < 2; i++)
        {
            bool got = false;
            TakeAll(@"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b", m =>
            {
                got = true;
                int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
                int year = m.Groups[3].Value.Length switch
                {
                    4 => int.Parse(m.Groups[3].Value),
                    2 => 2000 + int.Parse(m.Groups[3].Value),
                    _ => today.Year,
                };
                var d = dayFirst
                    ? SafeDate(year, b, a) ?? SafeDate(year, a, b)
                    : SafeDate(year, a, b) ?? SafeDate(year, b, a);
                if (d == null) return;
                if (m.Groups[3].Value.Length == 0 && d > today) d = d.Value.AddYears(-1);
                days.Add(d.Value);
                explicitDate = true;
            });
            if (!got) break;
        }
        TakeAll($@"\b({MonthsAlt})\.?\s+(\d{{1,2}})\s*[-–]\s*(\d{{1,2}})\b", m =>
        {
            var d1 = MonthDay(m.Groups[1].Value, int.Parse(m.Groups[2].Value), today);
            var d2 = MonthDay(m.Groups[1].Value, int.Parse(m.Groups[3].Value), today);
            if (d1 != null && d2 != null)
            {
                days.Add(d1.Value);
                days.Add(d2.Value);
                explicitDate = true;
            }
        });
        for (int i = 0; i < 2; i++)
        {
            bool got = false;
            TakeAll($@"\b({MonthsAlt})\.?\s+(\d{{1,2}})(?:st|nd|rd|th)?(?:,?\s+(20\d\d))?\b", m =>
            {
                got = true;
                var d = m.Groups[3].Success
                    ? MonthDayYear(m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
                    : MonthDay(m.Groups[1].Value, int.Parse(m.Groups[2].Value), today);
                if (d != null) { days.Add(d.Value); explicitDate = true; }
            });
            if (!got) break;
        }
        TakeAll($@"\b(\d{{1,2}})(?:st|nd|rd|th)?\s+(?:of\s+)?({MonthsAlt})\b", m =>
        {
            var d = MonthDay(m.Groups[2].Value, int.Parse(m.Groups[1].Value), today);
            if (d != null) { days.Add(d.Value); explicitDate = true; }
        });
        // A bare month means the whole month; "may" needs the "in" to avoid the verb.
        if (rFrom == null && days.Count == 0)
        {
            TakeAll($@"\bin\s+({MonthsAlt})(?:\s+(20\d\d)\b)?(?!\s*\d)",
                m => MonthRange(m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null,
                    today, ref rFrom, ref rTo));
            if (rFrom == null)
                TakeAll($@"\b({MonthsAlt.Replace("may|", "")})(?:\s+(20\d\d)\b)?(?!\s*\d)",
                    m => MonthRange(m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null,
                        today, ref rFrom, ref rTo));
        }
        TakeAll(@"\btoday\b", _ => days.Add(today));
        TakeAll(@"\byesterday\b", _ => days.Add(today.AddDays(-1)));
        // "mon-fri": abbreviations only count in the dash-pair form ("sat" alone
        // is a verb).
        Take(@"\b(mon|tues?|wed(?:nes)?|thur?s?|fri|sat(?:ur)?|sun)(?:day)?\s*[-–]\s*(mon|tues?|wed(?:nes)?|thur?s?|fri|sat(?:ur)?|sun)(?:day)?\b", m =>
        {
            int W(string w) => Array.FindIndex(DayNames, d => d.StartsWith(w[..3]));
            int w1 = W(m.Groups[1].Value), w2 = W(m.Groups[2].Value);
            if (w1 < 0 || w2 < 0) return false;
            var start = today.AddDays(-(((int)today.DayOfWeek - w1 + 7) % 7));
            rFrom = start;
            rTo = start.AddDays(((w2 - w1 + 7) % 7) + 1);
            return true;
        });
        // Weekdays: several may appear ("saturday and sunday"); an explicit date
        // already parsed wins over a misremembered weekday word.
        for (int i = 0; i < 3; i++)
        {
            bool matched = false;
            Take($@"\b(?:(last)\s+|on\s+)?({WeekdaysAlt})\b", m =>
            {
                matched = true;
                if (explicitDate) return true;
                int wd = Array.IndexOf(DayNames, m.Groups[2].Value);
                // "monday last week" resolves into the parsed range, not from today.
                var anchor = rFrom != null && rTo != null && (rTo.Value - rFrom.Value).TotalDays >= 2
                    ? rTo.Value.AddDays(-1).Date
                    : today;
                int back = ((int)anchor.DayOfWeek - wd + 7) % 7;
                if (back == 0 && anchor == today && m.Groups[1].Success) back = 7; // "last monday" asked on a Monday
                days.Add(anchor.AddDays(-back));
                return true;
            });
            if (!matched) break;
        }
        // Time ranges: "between 2 and 4pm", "from 2pm to 4", "2-4pm", "2pm to 4pm".
        // At least one am/pm or minute marker is required, so "between 2 and 4
        // people" never reads as 02:00-04:00.
        bool RangeOf(Match m)
        {
            string h1 = m.Groups[1].Value, mn1 = m.Groups[2].Value, a1 = m.Groups[3].Value, w1 = m.Groups[4].Value;
            string h2 = m.Groups[5].Value, mn2 = m.Groups[6].Value, a2 = m.Groups[7].Value, w2 = m.Groups[8].Value;
            bool word1 = w1.Length > 0, word2 = w2.Length > 0;
            // A marker disambiguates; a bare pair only counts when both hours
            // read as a 24h clock ("between 20 and 22").
            bool marked = a1.Length > 0 || a2.Length > 0 || mn1.Length > 0 || mn2.Length > 0 || word1 || word2;
            if (!marked && !(int.Parse(h1) is >= 13 and <= 23 && int.Parse(h2) is >= 13 and <= 23))
                return false;
            bool bareFirst = !word1 && a1.Length == 0 && a2.Length > 0;
            bool bareSecond = !word2 && a2.Length == 0 && (a1.Length > 0 || word1);
            int f = word1 ? (w1 == "noon" ? 720 : 0) : Mins(h1, mn1, bareFirst ? a2 : a1);
            int t = word2 ? (w2 == "noon" ? 720 : 1440) : Mins(h2, mn2, a2);
            if (bareSecond)
            {
                // The bare second hour takes whichever reading lies nearest AHEAD
                // of the first: "11pm and 1" → 01:00 next day, "9am and 5" → 17:00.
                int t1 = Mins(h2, mn2, "");
                int t2 = (t1 + 720) % 1440;
                t = (t1 - f + 1440) % 1440 <= (t2 - f + 1440) % 1440 ? t1 : t2;
            }
            if (word2 && !word1 && a1.Length == 0)
            {
                // "11 to midnight": the reading nearest BEHIND the fixed end wins.
                int f2 = (f + 720) % 1440;
                int end = t % 1440;
                if ((end - f2 + 1440) % 1440 < (end - f + 1440) % 1440) f = f2;
            }
            // "between 11 and 12pm": the am reading (11:00-12:00) beats a 13-hour
            // overnight cross when the first hour was bare.
            if (t <= f && bareFirst && f >= 720 && f - 720 < t) f -= 720;
            if (!word1 && !word2 && a1.Length == 0 && a2.Length == 0 && mn1.Length + mn2.Length > 0)
            {
                f = Daytime(f);
                t = Daytime(t);
            }
            tFrom = f;
            tTo = t;
            return true;
        }
        Take(@"\b(?:between|from)\s+(?:(\d{1,2})(?::(\d{2}))?\s*(am|pm)?|(noon|midnight))\s*(?:and|to|until|till|through|-|–)\s*(?:(\d{1,2})(?::(\d{2}))?\s*(am|pm)?|(noon|midnight))\b", RangeOf);
        if (tFrom == null)
            Take(@"\b(?:(\d{1,2})(?::(\d{2}))?\s*(am|pm)?|(noon|midnight))\s*(?:-|–|to|until|till)\s*(?:(\d{1,2})(?::(\d{2}))?\s*(am|pm)?|(noon|midnight))\b", RangeOf);
        TakeAll(@"\b(?:since|after)\s+(?:the\s+|of\s+)?(?:(\d{1,2})(?::(\d{2}))?\s*(am|pm)|noon|midnight)\b", m =>
        {
            int mins = m.Groups[1].Success
                ? Mins(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                : m.Value.TrimEnd().EndsWith("noon") ? 12 * 60 : 0;
            var anchor = (days.Count > 0 ? days[0] : rFrom?.Date ?? today).AddMinutes(mins);
            if (days.Count == 0 && rFrom == null && anchor > now) anchor = anchor.AddDays(-1); // "since 10pm" at 14:00
            qFrom = anchor;
        });
        TakeAll(@"\b(?:before|until|till|by)\s+(?:the\s+|of\s+)?(?:(\d{1,2})(?::(\d{2}))?\s*(am|pm)|noon|midnight)\b", m =>
        {
            int mins = m.Groups[1].Success
                ? Mins(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)
                : m.Value.TrimEnd().EndsWith("noon") ? 12 * 60 : 0;
            qTo = (days.Count > 0 ? days[0] : rFrom?.Date ?? today).AddMinutes(mins);
        });
        if (tFrom == null)
            TakeAll(@"\b(?:at\s+|around\s+|about\s+)?(\d{1,2}):(\d{2})\s*(am|pm)?\b", m =>
            {
                var v = Mins(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                tFrom = m.Groups[3].Value.Length == 0 ? Daytime(v) : v;
            });
        if (tFrom == null)
            TakeAll(@"\b(?:at|around|about)?\s?(\d{1,2})\s*(am|pm)\b",
                m => tFrom = Mins(m.Groups[1].Value, "", m.Groups[2].Value));
        if (tFrom == null) TakeAll(@"\b(?:at\s+)?noon\b", _ => tFrom = 12 * 60);
        if (tFrom == null) TakeAll(@"\b(?:at\s+)?midnight\b", _ => tFrom = 0);
        if (tFrom == null)
            Take(@"\b(\d{1,2})\s*o['’]?\s?clock\b", m =>
            {
                int h = int.Parse(m.Groups[1].Value);
                if (h > 23) return false;
                tFrom = BareHour(h) * 60;
                bareHour = true;
                return true;
            });
        if (tFrom == null)
            Take(@"\b(?:at|around|about)\s+(\d{1,2})\b(?!\s*(?::|am|pm|o))", m =>
            {
                int h = int.Parse(m.Groups[1].Value);
                if (h > 23) return false;
                tFrom = BareHour(h) * 60;
                bareHour = true;
                return true;
            });
        // Bare time-of-day words apply only when a day anchors them ("yesterday
        // morning"); alone they are consumed but set nothing.
        TakeAll(@"\b(?:in\s+the\s+|at\s+)?(morning|afternoon|evening|night|overnight)\b", m =>
        {
            todWord = m.Groups[1].Value;
            (todFrom, todTo) = todWord switch
            {
                "morning" => ((int?)(6 * 60), (int?)(12 * 60)),
                "afternoon" => (12 * 60, 18 * 60),
                "evening" => (18 * 60, 24 * 60),
                "night" => (20 * 60, 30 * 60),
                _ => (22 * 60, 30 * 60),
            };
        });
        // "at 3 in the morning": the time-of-day word overrides the bare-hour
        // daytime heuristic.
        if (bareHour && tFrom != null && todWord != null)
        {
            if (todWord == "morning" && tFrom >= 720) tFrom -= 720;
            else if (todWord is "night" or "evening" or "overnight" && tFrom < 720) tFrom += 720;
        }

        // "since last month" turned a closed range into an open bound.
        if (sinceRange && rFrom != null)
        {
            qFrom = rFrom;
            rFrom = null;
            rTo = null;
        }
        // "friday last week": a named day inside a parsed range wins over the range.
        if (days.Count > 0 && rFrom != null && rTo != null
            && days.Min() >= rFrom && days.Max() < rTo)
        {
            rFrom = null;
            rTo = null;
        }

        if (qFrom != null || qTo != null)
        {
            // "before 9am yesterday" / "last night before 9pm": the named day or
            // range phrase still bounds the other side.
            if (qTo != null && qFrom == null) qFrom = days.Count > 0 ? days.Min() : rFrom;
            // "monday until friday": a day-bound behind the floor means the NEXT one.
            if (qFrom != null && qTo != null && qTo <= qFrom && qTo.Value.TimeOfDay == TimeSpan.Zero)
                qTo = qTo.Value.AddDays(7);
            q.FromLocal = qFrom;
            q.ToLocal = qTo ?? (qFrom != null ? now.AddMinutes(1) : null);
        }
        else if (rFrom != null)
        {
            q.FromLocal = rFrom;
            q.ToLocal = rTo;
            // "last night at 11pm": narrow the range to the clock time inside it —
            // but only for short ranges; "last week at 3pm" must not collapse to
            // one hour of Monday.
            if (tFrom != null && rTo != null && rTo - rFrom <= TimeSpan.FromHours(36))
            {
                var cand = rFrom.Value.Date.AddMinutes(tFrom.Value);
                if (cand < rFrom) cand = cand.AddDays(1);
                if ((cand < rFrom || cand >= rTo) && bareHour)
                {
                    // "around 9 last night": the reading inside the window wins.
                    var alt = rFrom.Value.Date.AddMinutes((tFrom.Value + 720) % 1440);
                    if (alt < rFrom) alt = alt.AddDays(1);
                    if (alt >= rFrom && alt < rTo) cand = alt;
                }
                if (cand >= rFrom && cand < rTo)
                {
                    // "between 10pm and 2am": the end reads on the far side of midnight.
                    int dur = tTo == null ? 60
                        : tTo.Value > tFrom.Value ? tTo.Value - tFrom.Value
                        : tTo.Value + 1440 - tFrom.Value;
                    q.FromLocal = cand;
                    q.ToLocal = cand.AddMinutes(dur);
                }
            }
        }
        else if (days.Count > 0)
        {
            var d0 = days.Min();
            var d1 = days.Max();
            int? f = tFrom ?? todFrom, t = tFrom != null ? tTo : todTo;
            if (days.Count == 1 && f != null)
            {
                if (t != null && t <= f) t += 24 * 60; // crosses midnight
                q.FromLocal = d0.AddMinutes(f.Value);
                q.ToLocal = d0.AddMinutes(t ?? f.Value + 60);
            }
            else
            {
                q.FromLocal = d0;
                q.ToLocal = d1.AddDays(1);
            }
        }
        else if (tFrom != null)
        {
            if (tTo != null && tTo <= tFrom) tTo += 24 * 60;
            q.FromLocal = today.AddMinutes(tFrom.Value);
            q.ToLocal = today.AddMinutes(tTo ?? tFrom.Value + 60);
            q.TimeOnly = true;
        }
        else if (todFrom != null)
        {
            // A bare time-of-day anchors to its most recent instance —
            // "overnight" means the night just past.
            var b = todWord is "night" or "overnight" ? today.AddDays(-1) : today;
            q.FromLocal = b.AddMinutes(todFrom.Value);
            q.ToLocal = b.AddMinutes(todTo!.Value);
        }
        return s;
    }

    private static DateTime Back(DateTime now, DateTime today, int n, string unit) => unit switch
    {
        "minute" => now.AddMinutes(-n),
        "hour" => now.AddHours(-n),
        "week" => today.AddDays(-7 * n),
        "month" => today.AddMonths(-n),
        _ => today.AddDays(-n),
    };

    private static void MonthRange(string mon, int? year, DateTime today, ref DateTime? rFrom, ref DateTime? rTo)
    {
        int m = Array.IndexOf(MonthNames, mon[..3]) + 1;
        if (m == 0) return;
        var first = new DateTime(year ?? today.Year, m, 1);
        if (year == null && first > today) first = first.AddYears(-1);
        rFrom = first;
        rTo = first.AddMonths(1);
    }

    /// <summary>1:00-6:59 with no meridiem reads as afternoon/evening — a
    /// security query's "2:30" is almost never small hours.</summary>
    private static int Daytime(int mins) => mins is >= 60 and < 420 ? mins + 720 : mins;

    private static DateTime? MonthDayYear(string mon, int d, int year)
    {
        int m = Array.IndexOf(MonthNames, mon[..3]) + 1;
        return m == 0 ? null : SafeDate(year, m, d);
    }

    private static int WordNum(string w) => w switch
    {
        "a" or "an" or "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        "few" => 3,
        _ => w.StartsWith("couple") ? 2 : int.Parse(w),
    };

    /// <summary>A bare hour with no am/pm: 1-6 read as afternoon/evening (a
    /// security query's "at 3" is almost never 03:00), the rest literally.</summary>
    private static int BareHour(int h) => h is >= 1 and <= 6 ? h + 12 : h;

    private static IEnumerable<string> CameraVariants(string name)
    {
        var lower = name.ToLowerInvariant();
        yield return lower;
        var spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").ToLowerInvariant();
        if (spaced != lower) yield return spaced;
        var squashed = lower.Replace(" ", "");
        if (squashed != lower) yield return squashed;
    }

    private static int Mins(string hour, string minute, string ampm)
    {
        int h = int.Parse(hour), m = minute.Length > 0 ? int.Parse(minute) : 0;
        if (ampm == "pm" && h < 12) h += 12;
        if (ampm == "am" && h == 12) h = 0;
        return h * 60 + m;
    }

    private static DateTime? SafeDate(int y, int m, int d) =>
        y >= 2000 && y <= 2100 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
            ? new DateTime(y, m, d) : null;

    private static DateTime? MonthDay(string mon, int d, DateTime today)
    {
        if (d < 1) return null;
        int m = Array.IndexOf(MonthNames, mon[..3]) + 1;
        if (m == 0) return null;
        var dt = new DateTime(today.Year, m, Math.Min(d, DateTime.DaysInMonth(today.Year, m)));
        return dt > today ? dt.AddYears(-1) : dt; // "aug 20" said in march means last year's
    }

    public static List<EventRecord> Execute(EventQuery q, EventStore store, int limit = 200) =>
        Execute(q, store, out _, out _, limit);

    public static List<EventRecord> Execute(EventQuery q, EventStore store, out bool keywordsMatched,
        int limit = 200) => Execute(q, store, out keywordsMatched, out _, limit);

    public static List<EventRecord> Execute(EventQuery q, EventStore store, out bool keywordsMatched,
        out bool keywordsPartial, int limit = 200)
    {
        keywordsMatched = q.Keywords.Count == 0;
        keywordsPartial = false;
        string? cam = q.Cameras.Count == 1 ? q.Cameras[0] : null;
        // One-sided ranges get a synthesized bound so narrow windows walk the
        // per-day index instead of scanning the whole store.
        DateTime? df0 = q.FromLocal ?? (q.ToLocal != null ? q.ToLocal.Value.AddDays(-31) : null);
        DateTime? dt0 = q.ToLocal ?? (q.FromLocal != null ? DateTime.Now.AddMinutes(1) : null);
        IEnumerable<EventRecord> hits;
        if (df0 is { } df && dt0 is { } dt && (dt.Date - df.Date).Days <= 31)
        {
            var acc = new List<EventRecord>();
            for (var d = df.Date; d < dt; d = d.AddDays(1))
                acc.AddRange(store.List(cam, null, 10_000, d, excludeWakeOnly: true));
            hits = acc;
        }
        else if (q.Cameras.Count > 1)
        {
            var acc = new List<EventRecord>();
            foreach (var c in q.Cameras)
                acc.AddRange(store.List(c, limit: 100_000, excludeWakeOnly: true));
            hits = acc;
        }
        else
        {
            // No timeframe means ALL of it: filters must see the whole retained
            // index, not the newest slice of a busy system.
            hits = store.List(cam, limit: 100_000, excludeWakeOnly: true);
        }
        if (q.Cameras.Count > 1)
            hits = hits.Where(e => q.Cameras.Contains(e.Camera, StringComparer.OrdinalIgnoreCase));
        if (q.FromLocal is { } f2) hits = hits.Where(e => e.StartUtc.ToLocalTime() >= f2);
        if (q.ToLocal is { } t2) hits = hits.Where(e => e.StartUtc.ToLocalTime() < t2);
        if (q.Labels.Count > 0) hits = hits.Where(e => e.Labels.Any(l => q.Labels.Contains(l)));
        if (q.NotLabels.Count > 0) hits = hits.Where(e => !e.Labels.Any(l => q.NotLabels.Contains(l)));
        if (q.Keywords.Count == 0)
            return hits.OrderByDescending(e => e.StartUtc).Take(limit).ToList();
        var pool = hits.ToList();
        var scored = pool.Select(e => { int s = Score(e, q.Keywords, out int hit); return (e, s, hit); })
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s).ThenByDescending(x => x.e.StartUtc)
            .ToList();
        // A specific query stays specific: events matching every keyword shut
        // out the ones that only matched some ("gray shirt" must not surface
        // every shirt).
        var full = scored.Where(x => x.hit >= q.Keywords.Count).Select(x => x.e).Take(limit).ToList();
        if (full.Count > 0)
        {
            keywordsMatched = true;
            return full;
        }
        if (scored.Count > 0)
        {
            keywordsMatched = true;
            keywordsPartial = true;
            return scored.Select(x => x.e).Take(limit).ToList();
        }
        // Keywords narrow, never to zero: many events carry no AI description to
        // match against, so when structured filters exist their hits stand.
        return q.HasStructure ? pool.OrderByDescending(e => e.StartUtc).Take(limit).ToList() : new List<EventRecord>();
    }

    /// <summary>Whole-word keyword scoring with plural tolerance — substring
    /// matching let "red" hit "covered" and "cat" hit "scattered".</summary>
    internal static int Score(EventRecord e, IReadOnlyList<string> keywords) => Score(e, keywords, out _);

    internal static int Score(EventRecord e, IReadOnlyList<string> keywords, out int hit)
    {
        HashSet<string>? words = null;
        if (e.AiDescription is { } d)
            words = new HashSet<string>(
                Regex.Split(d.ToLowerInvariant(), "[^a-z0-9]+").Select(Spelling), StringComparer.Ordinal);
        int s = 0;
        hit = 0;
        foreach (var k0 in keywords)
        {
            bool found = false;
            var k = Spelling(k0);
            if (words != null && (words.Contains(k)
                || words.Contains(k + "s")
                || words.Contains(k + "es")
                || (k.EndsWith("y") && words.Contains(k[..^1] + "ies"))
                || (k.EndsWith("ies") && words.Contains(k[..^3] + "y"))
                || (k.EndsWith("es") && words.Contains(k[..^2]))
                || (k.EndsWith("s") && words.Contains(k[..^1]))))
            {
                s += 2;
                found = true;
            }
            if (LevelWords.TryGetValue(k, out var lvl)
                && string.Equals(e.AiLevel, lvl, StringComparison.OrdinalIgnoreCase))
            {
                s++;
                found = true;
            }
            if (found) hit++;
        }
        return s;
    }

    /// <summary>British query, American description (the models write "gray").</summary>
    private static string Spelling(string w) => w switch
    {
        "grey" => "gray",
        "colour" => "color",
        "tyre" => "tire",
        "moustache" => "mustache",
        "pyjamas" => "pajamas",
        "jewellery" => "jewelry",
        _ => w,
    };

    /// <summary>AI fills gaps; the deterministic parse always wins where it
    /// produced something — the grammar's dates are exact, a small model's rarely
    /// are (live: the model turned an already-parsed "last week" into a rolling
    /// seven days anchored to the current minute).</summary>
    public static EventQuery Merge(EventQuery parsed, EventQuery ai)
    {
        bool parsedDates = (parsed.FromLocal != null || parsed.ToLocal != null)
            && !(parsed.TimeOnly && ai.FromLocal != null);
        var q = new EventQuery
        {
            FromLocal = parsedDates ? parsed.FromLocal : ai.FromLocal,
            ToLocal = parsedDates ? parsed.ToLocal : ai.ToLocal,
        };
        foreach (var l in parsed.Labels.Concat(ai.Labels))
            if (!q.Labels.Contains(l)) q.Labels.Add(l);
        foreach (var l in parsed.NotLabels)
            if (!q.NotLabels.Contains(l)) q.NotLabels.Add(l);
        q.Labels.RemoveAll(q.NotLabels.Contains); // a negated label never sneaks back in
        foreach (var c in parsed.Cameras.Concat(ai.Cameras))
            if (!q.Cameras.Contains(c)) q.Cameras.Add(c);
        foreach (var k in parsed.Keywords.Concat(ai.Keywords))
            if (!q.Keywords.Contains(k)) q.Keywords.Add(k);
        // A selected camera already encodes its place words — "yard" alongside
        // Backyard can never match a description and poisons the all-keywords
        // tier. Substring shadowing ("green" in "Greenhouse") must never delete
        // the LAST keywords, or the descriptive part of the query vanishes.
        if (q.Cameras.Count > 0 && q.Keywords.Count > 0)
        {
            var variants = q.Cameras.SelectMany(CameraVariants).ToList();
            q.Keywords.RemoveAll(k => variants.Any(v => v.Split(' ').Contains(k)));
            var shadowed = q.Keywords
                .Where(k => k.Length >= 4 && variants.Any(v => v.Contains(k))).ToList();
            if (shadowed.Count < q.Keywords.Count) q.Keywords.RemoveAll(shadowed.Contains);
        }
        return q;
    }

    // Worked examples and precomputed dates: small local models copy what they
    // see; asked to compute "last week" themselves they anchor to the current
    // minute instead of calendar days.
    public static string TranslateSystemPrompt(IReadOnlyList<string> cameras, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var camList = cameras.Count == 0
            ? "(none configured)"
            : string.Join(", ", cameras.Select(c => "\"" + c + "\""));
        var camShot = cameras.Count == 0 ? "" :
            $"Example — \"anyone at {cameras[0]} in the evening\" -> {{\"labels\":[\"person\"],\"cameras\":[\"{cameras[0]}\"]," +
            $"\"from\":\"{today:yyyy-MM-dd} 18:00\",\"to\":\"{today.AddDays(1):yyyy-MM-dd} 00:00\",\"keywords\":[]}}\n";
        return
            "You translate a security-camera search query into a JSON filter. The query may be in any language; " +
            "the filter is ALWAYS English.\n" +
            "Answer with ONE line: a single valid JSON object. No prose, no markdown fences, do not repeat the " +
            "schema, never output a second object.\n" +
            "All five fields are always present: {\"labels\":[],\"cameras\":[],\"from\":null,\"to\":null,\"keywords\":[]}\n" +
            "Rules:\n" +
            "- labels: zero or more of exactly person, vehicle, animal, package, doorbell, motion, crying, " +
            "line-crossing, intrusion, loitering. Map words in any language: car/truck/bike -> vehicle, " +
            "someone/man/woman/kid -> person, dog/cat -> animal. Never output any other label.\n" +
            $"- cameras: only names copied character-for-character from this list: {camList}. Several may match. " +
            "A place this list does not name is NOT a camera: leave cameras alone and put the place word, in " +
            "English, into keywords instead.\n" +
            $"- from and to: \"yyyy-MM-dd HH:mm\" strings, or null. Now is {nowLocal:yyyy-MM-dd HH:mm}, a " +
            $"{nowLocal.DayOfWeek}. Whole days ALWAYS run 00:00 to 00:00 of the NEXT day — never the current " +
            $"clock time. \"yesterday\" = \"{today.AddDays(-1):yyyy-MM-dd} 00:00\" to \"{today:yyyy-MM-dd} 00:00\". " +
            $"\"last week\" = the previous calendar week, \"{monday.AddDays(-7):yyyy-MM-dd} 00:00\" to " +
            $"\"{monday:yyyy-MM-dd} 00:00\". \"this week\" = \"{monday:yyyy-MM-dd} 00:00\" to " +
            $"\"{today.AddDays(1):yyyy-MM-dd} 00:00\". Compute every other date or time phrase, in any language, " +
            "from Now the same way. If the query has no date or time phrase, or you cannot work the range out, " +
            "use null for BOTH.\n" +
            "- keywords: 0 to 5 lowercase ENGLISH words that would appear in a written English description of " +
            "the scene (colors, objects, actions). Translate them to English. Never repeat a label word or a " +
            "camera name. At most one spelling variant (grey/gray).\n" +
            $"Example — \"blue truck at the gate yesterday\" -> {{\"labels\":[\"vehicle\"],\"cameras\":[]," +
            $"\"from\":\"{today.AddDays(-1):yyyy-MM-dd} 00:00\",\"to\":\"{today:yyyy-MM-dd} 00:00\"," +
            "\"keywords\":[\"blue\",\"gate\"]}\n" +
            camShot +
            $"Example — \"coche rojo ayer en la entrada\" -> {{\"labels\":[\"vehicle\"],\"cameras\":[]," +
            $"\"from\":\"{today.AddDays(-1):yyyy-MM-dd} 00:00\",\"to\":\"{today:yyyy-MM-dd} 00:00\"," +
            "\"keywords\":[\"red\",\"entrance\"]}\n" +
            "Example — \"when did the mailman last come\" -> {\"labels\":[\"person\"],\"cameras\":[]," +
            "\"from\":null,\"to\":null,\"keywords\":[\"mailman\",\"mail\",\"postal\"]}";
    }

    /// <summary>Candidates for the judge pass: every keyword-hit event first — a
    /// description that literally mentions the words must never age out of the
    /// pool on a busy system — then newest described events fill the rest for
    /// the paraphrases keywords cannot see.</summary>
    public static List<EventRecord> JudgePool(EventQuery q, EventStore store)
    {
        var structural = Execute(q.StructuralOnly(), store, 10_000)
            .Where(e => !string.IsNullOrWhiteSpace(e.AiDescription)).ToList();
        var hit = structural.Where(e => Score(e, q.Keywords) > 0).Take(200).ToList();
        var ids = new HashSet<string>(hit.Select(e => e.Id), StringComparer.Ordinal);
        return hit.Concat(structural.Where(e => !ids.Contains(e.Id))).Take(300).ToList();
    }

    /// <summary>The judge pass: the model reads real event descriptions and picks
    /// the ones matching the query's descriptive part — paraphrase-proof where
    /// keyword matching is not ("gray" never matches "light-colored").</summary>
    public static string JudgeSystemPrompt() =>
        "You match security-camera events against a search query.\n" +
        "The time, camera and event-type filters are ALREADY applied — judge ONLY whether each event's " +
        "description fits the descriptive part of the query (colors, clothing, objects, actions, details). " +
        "The query may be in any language.\n" +
        "Be strict: include an event only when its description actually supports the query. A query asking " +
        "for \"red\" does not match \"light-colored\" or \"striped\". Treat synonyms and spelling variants " +
        "as matches (grey = gray, car = vehicle).\n" +
        "Answer with ONE line: a JSON array of the matching event numbers, e.g. [2,5]. No prose. " +
        "If none match, answer [].";

    public static string JudgeUserPrompt(string query, IReadOnlyList<EventRecord> chunk)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Query: ").Append(query.Trim()).Append("\nEvents:\n");
        for (int i = 0; i < chunk.Count; i++)
        {
            var d = (chunk[i].AiDescription ?? "").Replace('\n', ' ').Trim();
            if (d.Length > 400) d = d[..400];
            sb.Append(i + 1).Append(". ").Append(d).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Numbers out of the judge's reply; null when the reply is missing
    /// or garbage (so the caller can fall back to keyword scoring).</summary>
    public static List<int>? ParseJudge(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Last bracketed array wins: models echo the prompt's "[2,5]" example
        // before answering.
        var brackets = Regex.Matches(raw, @"\[[^\[\]]*\]");
        if (brackets.Count == 0
            && Regex.IsMatch(raw, @"\b(none|no events?|nothing|no match(?:es)?|do(?:es)? not match|neither)\b",
                RegexOptions.IgnoreCase))
            return new List<int>();
        var src = brackets.Count > 0 ? brackets[^1].Value : raw;
        var ids = Regex.Matches(src, @"\d+").Where(x => x.Value.Length <= 4)
            .Select(x => int.Parse(x.Value))
            .Where(i => i >= 1 && i <= max).Distinct().ToList();
        if (brackets.Count == 0 && ids.Count == 0) return null;
        return ids;
    }

    /// <summary>The model's answer (fences, prose, echoed examples, sloppy JSON
    /// tolerated) → a plan, or null when nothing usable survives.</summary>
    public static EventQuery? ParseTranslated(string? raw, IReadOnlyList<string> cameraNames, DateTime nowLocal)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var best = BestPlan(raw, cameraNames, nowLocal);
        if (best == null)
        {
            // Python-dict repair: single quotes and None/True/False.
            var repaired = Regex.Replace(raw.Replace('\'', '"'), @"\b(None|True|False)\b",
                m => m.Value == "None" ? "null" : m.Value.ToLowerInvariant());
            best = BestPlan(repaired, cameraNames, nowLocal);
        }
        return best is { IsEmpty: false } ? best : null; // an empty plan refines nothing
    }

    private static EventQuery? BestPlan(string raw, IReadOnlyList<string> cameraNames, DateTime nowLocal)
    {
        EventQuery? best = null;
        // Balanced objects, tried in order; echoed few-shot examples precede the
        // real answer, so the LAST non-empty parse wins.
        foreach (Match m in Regex.Matches(raw, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}"))
        {
            var plan = TryPlan(m.Value, cameraNames, nowLocal);
            if (plan != null && (best == null || !plan.IsEmpty)) best = plan;
        }
        return best;
    }

    private static readonly JsonDocumentOptions TolerantJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string[] PlanKeys = { "labels", "cameras", "from", "to", "keywords" };

    private static EventQuery? TryPlan(string json, IReadOnlyList<string> cameraNames, DateTime nowLocal)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, TolerantJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // Small models sometimes wrap the answer ({"filter": {...}} or
            // {"data": [{...}]}).
            if (!PlanKeys.Any(k => Prop(root, k) != null))
            {
                var inner = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).ToList();
                if (inner.Count == 1)
                {
                    root = inner[0].Value;
                }
                else
                {
                    var arr = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array).ToList();
                    if (arr.Count == 1 && arr[0].Value.GetArrayLength() > 0
                        && arr[0].Value[0].ValueKind == JsonValueKind.Object)
                        root = arr[0].Value[0];
                }
            }
            var q = new EventQuery();
            foreach (var l in Strs(root, "labels"))
            {
                var canon = LabelOf(l.Trim().ToLowerInvariant());
                if (canon != null && !q.Labels.Contains(canon)) q.Labels.Add(canon);
            }
            foreach (var c in Strs(root, "cameras"))
            {
                var hit = MatchCamera(c, cameraNames);
                if (hit != null)
                {
                    if (!q.Cameras.Contains(hit)) q.Cameras.Add(hit);
                    continue;
                }
                // An unmatched "camera" is a place cue — keep its words as keywords.
                foreach (var w in Regex.Split(c.ToLowerInvariant(), @"[^a-z0-9]+"))
                    if (w.Length >= 3 && LabelOf(w) == null && !Filler.Contains(w) && !q.Keywords.Contains(w))
                        q.Keywords.Add(w);
            }
            q.FromLocal = PlanDate(root, "from", nowLocal, wantEnd: false);
            q.ToLocal = PlanDate(root, "to", nowLocal, wantEnd: true);
            if (q.ToLocal <= q.FromLocal)
                // Degenerate model range: an equal date-only pair means that whole day.
                q.ToLocal = q.ToLocal == q.FromLocal && q.FromLocal == q.FromLocal!.Value.Date
                    ? q.FromLocal.Value.AddDays(1)
                    : q.FromLocal!.Value.AddHours(1);
            foreach (var k in Strs(root, "keywords"))
            {
                var kk = k.Trim().ToLowerInvariant();
                if (kk.Length < 3 || q.Keywords.Count >= 6 || q.Keywords.Contains(kk)) continue;
                var canon = LabelOf(kk);
                if (canon != null)
                {
                    if (!q.Labels.Contains(canon)) q.Labels.Add(canon);
                    continue;
                }
                if (MatchCamera(kk, cameraNames) != null) continue;
                q.Keywords.Add(kk);
            }
            return q;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? Prop(JsonElement root, string name)
    {
        // Last wins: models echo the empty schema key then repeat it filled.
        JsonElement? hit = null;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) hit = p.Value;
        return hit;
    }

    private static List<string> Strs(JsonElement root, string name)
    {
        var el = Prop(root, name);
        if (el is not { } v) return new();
        if (v.ValueKind == JsonValueKind.String)
        {
            var one = v.GetString() ?? "";
            // A lone string where an array belongs; keywords may pack several words.
            return name == "keywords"
                ? Regex.Split(one, @"[,;/]|\s+").Where(x => x.Length > 0).ToList()
                : new List<string> { one };
        }
        return v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).ToList()
            : new();
    }

    private static string? MatchCamera(string value, IReadOnlyList<string> cameraNames)
    {
        string? Try(string s)
        {
            var sq = s.Replace(" ", "").ToLowerInvariant();
            return sq.Length == 0 ? null
                : cameraNames.FirstOrDefault(n => n.Replace(" ", "").ToLowerInvariant() == sq);
        }
        var v = value.Trim();
        return Try(v) ?? Try(Regex.Replace(v, @"\s+(camera|cam)$", "", RegexOptions.IgnoreCase));
    }

    private static DateTime? PlanDate(JsonElement root, string name, DateTime nowLocal, bool wantEnd)
    {
        var el = Prop(root, name);
        if (el is not { ValueKind: JsonValueKind.String } v) return null;
        var raw = (v.GetString() ?? "").Trim().TrimEnd('Z', 'z');
        if (raw.Length == 0) return null;
        foreach (var fmt in new[]
        {
            "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd", "MM/dd/yyyy HH:mm", "MM/dd/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
        })
            if (DateTime.TryParseExact(raw, fmt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                return d;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d2))
            return d2;
        // Word dates ("yesterday", "last week"): the grammar already knows them.
        var tmp = new EventQuery();
        ParseDates(" " + raw.ToLowerInvariant() + " ", tmp, nowLocal);
        if (!wantEnd) return tmp.FromLocal;
        var end = tmp.ToLocal ?? tmp.FromLocal;
        // "2 hours ago" in the to-slot means that moment, not the rolling window's
        // now-anchored end.
        if (end != null && (nowLocal - end.Value).Duration() <= TimeSpan.FromMinutes(2))
            end = tmp.FromLocal;
        return end;
    }
}
