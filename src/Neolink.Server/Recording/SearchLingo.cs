// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using System.Text;
using System.Text.RegularExpressions;

namespace Neolink.Recording;

/// <summary>Multilingual front-end for EventSearch: rewrites the closed
/// vocabulary (dates, labels, negators, connectives) of the six UI languages
/// into English so the one grammar serves them all. Open vocabulary is left
/// alone — untranslated words become keywords and take the AI-translate path.</summary>
internal static class SearchLingo
{
    static SearchLingo()
    {
        // Queries are typed lazily: every accented key also matches unaccented.
        foreach (var map in Maps.Values)
            foreach (var (k, v) in map.ToArray())
            {
                var f = Fold(k);
                if (f != k) map.TryAdd(f, v);
            }
    }

    /// <summary>Translates s in place when a supported language beats English
    /// on token count. dayFirst: continental queries read 3/8 as 3 August.</summary>
    public static string Normalize(string s, out bool dayFirst)
    {
        dayFirst = false;
        s = s.Replace('’', '\'');
        var tokens = Regex.Matches(s, @"[\p{L}\p{N}]+(?:['-][\p{L}\p{N}]+)*")
            .Select(m => m.Value).ToList();
        string? best = null;
        int bestScore = 0;
        foreach (var (lang, map) in Maps)
        {
            int score = tokens.Count(map.ContainsKey);
            if (score > bestScore) { best = lang; bestScore = score; }
        }
        // Strictly beat English: "pies yesterday" is English pastry, not Polish dogs.
        if (best == null || bestScore <= tokens.Count(EventSearch.EnglishVocab.Contains))
        {
            s = Regex.Replace(s, @"[\p{L}\p{N}]+",
                m => Slang.TryGetValue(m.Value, out var v) ? v : m.Value);
            return Fold(s);
        }
        dayFirst = true;
        var chosen = Maps[best];
        // Prefix-ago becomes the grammar's postfix form before tokens translate;
        // only a following unit word triggers it, so es "ha llegado" stays put.
        var agoPrefix = best switch
        {
            "fr" => @"\bil y a",
            "es" => @"\b(?:hace|h[áa])",
            "pt" => @"\bh[áa]",
            "de" => @"\bvor",
            _ => null,
        };
        if (agoPrefix != null)
            s = Regex.Replace(s, agoPrefix + @"\s+(\S+)\s+(\p{L}+)", m =>
                chosen.TryGetValue(m.Groups[2].Value, out var u) && AgoUnits.Contains(u)
                    ? m.Groups[1].Value + " " + m.Groups[2].Value + " ago"
                    : m.Value);
        if (best == "es") s = Regex.Replace(s, @"\bfin de semana\b", "weekend");
        if (best == "pt") s = Regex.Replace(s, @"\bfim de semana\b", "weekend");
        if (best is "fr" or "pt")
            s = Regex.Replace(s, @"\b(\d{1,2})h(\d{2})?\b",
                m => m.Groups[1].Value + ":" + (m.Groups[2].Success ? m.Groups[2].Value : "00"));
        s = Regex.Replace(s, @"[\p{L}\p{N}]+(?:['-][\p{L}\p{N}]+)*",
            m => chosen.TryGetValue(m.Value, out var en) ? en : m.Value);
        // Postpositive adjectives land backwards: "week last" → "last week".
        s = Regex.Replace(s,
            @"\b(weekend|weeks?|months?|nights?|days?|hours?|mornings?|afternoons?|evenings?|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\s+(last|this|past)\b",
            "$2 $1");
        s = Regex.Replace(s, @"\b(\d{1,3})\s+(last|past)\s+(minutes?|hours?|days?|weeks?|months?)\b", "$2 $1 $3");
        s = Regex.Replace(s, @"\b(at|around|between)\s+the\s+(\d)", "$1 $2"); // "a las 3"
        return Fold(s);
    }

    /// <summary>Diacritics fold to ASCII so leftover keywords still tokenize.
    /// Explicit table: InvariantGlobalization makes Normalize(FormD) a no-op.</summary>
    private static string Fold(string s)
    {
        if (s.All(char.IsAscii)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            switch (c)
            {
                case 'á' or 'à' or 'â' or 'ã' or 'ä' or 'å' or 'ą': sb.Append('a'); break;
                case 'ç' or 'ć': sb.Append('c'); break;
                case 'é' or 'è' or 'ê' or 'ë' or 'ę': sb.Append('e'); break;
                case 'í' or 'ì' or 'î' or 'ï': sb.Append('i'); break;
                case 'ñ' or 'ń': sb.Append('n'); break;
                case 'ó' or 'ò' or 'ô' or 'õ' or 'ö' or 'ø': sb.Append('o'); break;
                case 'ú' or 'ù' or 'û' or 'ü': sb.Append('u'); break;
                case 'ý' or 'ÿ': sb.Append('y'); break;
                case 'ś': sb.Append('s'); break;
                case 'ź' or 'ż': sb.Append('z'); break;
                case 'ł': sb.Append('l'); break;
                case 'ß': sb.Append("ss"); break;
                case 'œ': sb.Append("oe"); break;
                case 'æ': sb.Append("ae"); break;
                default: sb.Append(c); break;
            }
        return sb.ToString();
    }

    private static readonly HashSet<string> AgoUnits = new(StringComparer.Ordinal)
        { "minutes", "hour", "hours", "day", "days", "week", "weeks", "month", "months" };

    // English texting shorthand, applied only on the English path.
    private static readonly Dictionary<string, string> Slang = new(StringComparer.Ordinal)
    {
        ["nite"] = "night", ["tonite"] = "tonight", ["2day"] = "today", ["2nite"] = "tonight",
        ["yday"] = "yesterday", ["yest"] = "yesterday", ["btwn"] = "between", ["thru"] = "until",
        ["wk"] = "week", ["wks"] = "weeks", ["hr"] = "hour", ["hrs"] = "hours", ["mins"] = "minutes",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Maps = new()
    {
        ["fr"] = new(StringComparer.Ordinal)
        {
            ["hier"] = "yesterday", ["aujourd'hui"] = "today", ["avant-hier"] = "day before yesterday",
            ["matin"] = "morning",
            ["matinée"] = "morning", ["après-midi"] = "afternoon", ["soir"] = "evening",
            ["soirée"] = "evening", ["nuit"] = "night", ["midi"] = "noon", ["minuit"] = "midnight",
            ["dernier"] = "last", ["dernière"] = "last", ["derniers"] = "last", ["dernières"] = "last",
            ["passé"] = "past", ["passée"] = "past", ["animal"] = "animal", ["semaine"] = "week", ["semaines"] = "weeks",
            ["mois"] = "month", ["jour"] = "day", ["jours"] = "days", ["journée"] = "day",
            ["heure"] = "hour", ["heures"] = "hours", ["minutes"] = "minutes",
            ["week-end"] = "weekend", ["depuis"] = "since", ["avant"] = "before", ["après"] = "after",
            ["jusqu'à"] = "until", ["entre"] = "between", ["vers"] = "around", ["à"] = "at",
            ["et"] = "and", ["ou"] = "or", ["sans"] = "without", ["aucun"] = "no", ["aucune"] = "no",
            ["pas"] = "not", ["le"] = "the", ["la"] = "the", ["les"] = "the", ["de"] = "the",
            ["du"] = "the", ["des"] = "the", ["un"] = "a", ["une"] = "a", ["ce"] = "this",
            ["cet"] = "this", ["cette"] = "this", ["sur"] = "on", ["dans"] = "in",
            ["pendant"] = "during",
            ["voiture"] = "car", ["voitures"] = "cars", ["véhicule"] = "vehicle",
            ["véhicules"] = "vehicles", ["camion"] = "truck", ["moto"] = "motorcycle",
            ["chien"] = "dog", ["chiens"] = "dogs", ["chat"] = "cat", ["chats"] = "cats",
            ["animaux"] = "animals", ["oiseau"] = "bird", ["personne"] = "person",
            ["personnes"] = "people", ["gens"] = "people", ["homme"] = "man", ["femme"] = "woman",
            ["quelqu'un"] = "someone", ["enfant"] = "child", ["colis"] = "package",
            ["livraison"] = "delivery", ["sonnette"] = "doorbell", ["visiteur"] = "visitor",
            ["mouvement"] = "motion", ["suspect"] = "suspicious", ["suspecte"] = "suspicious",
            ["dangereux"] = "dangerous",
            ["lundi"] = "monday", ["mardi"] = "tuesday", ["mercredi"] = "wednesday",
            ["jeudi"] = "thursday", ["vendredi"] = "friday", ["samedi"] = "saturday",
            ["dimanche"] = "sunday",
            ["janvier"] = "january", ["février"] = "february", ["mars"] = "march",
            ["avril"] = "april", ["mai"] = "may", ["juin"] = "june", ["juillet"] = "july",
            ["août"] = "august", ["septembre"] = "september", ["octobre"] = "october",
            ["novembre"] = "november", ["décembre"] = "december",
            ["deux"] = "two", ["trois"] = "three", ["quatre"] = "four", ["cinq"] = "five",
            ["sept"] = "seven", ["huit"] = "eight", ["neuf"] = "nine", ["dix"] = "ten",
        },
        ["de"] = new(StringComparer.Ordinal)
        {
            ["gestern"] = "yesterday", ["heute"] = "today", ["vorgestern"] = "day before yesterday",
            ["morgens"] = "morning", ["morgen"] = "morning", ["vormittag"] = "morning",
            ["nachmittag"] = "afternoon", ["nachmittags"] = "afternoon", ["abend"] = "evening",
            ["abends"] = "evening", ["nacht"] = "night", ["nachts"] = "night", ["mittag"] = "noon",
            ["mitternacht"] = "midnight", ["letzte"] = "last", ["letzten"] = "last",
            ["letzter"] = "last", ["letztes"] = "last", ["vergangene"] = "past",
            ["vergangenen"] = "past", ["woche"] = "week", ["wochen"] = "weeks", ["monat"] = "month",
            ["monate"] = "months", ["monaten"] = "months", ["tag"] = "day", ["tage"] = "days",
            ["tagen"] = "days", ["stunde"] = "hour", ["stunden"] = "hours", ["minuten"] = "minutes",
            ["wochenende"] = "weekend", ["seit"] = "since", ["bis"] = "until", ["vor"] = "before",
            ["nach"] = "after", ["zwischen"] = "between", ["von"] = "from", ["um"] = "at",
            ["gegen"] = "around", ["und"] = "and", ["oder"] = "or", ["ohne"] = "without",
            ["kein"] = "no", ["keine"] = "no", ["keinen"] = "no", ["nicht"] = "not",
            ["der"] = "the", ["die"] = "the", ["das"] = "the", ["den"] = "the", ["dem"] = "the",
            ["ein"] = "a", ["eine"] = "a", ["einen"] = "a", ["diese"] = "this", ["dieser"] = "this",
            ["dieses"] = "this", ["im"] = "in", ["am"] = "on", ["während"] = "during",
            ["auto"] = "car", ["autos"] = "cars", ["wagen"] = "car", ["fahrzeug"] = "vehicle",
            ["fahrzeuge"] = "vehicles", ["lastwagen"] = "truck", ["lkw"] = "truck",
            ["motorrad"] = "motorcycle", ["hund"] = "dog", ["hunde"] = "dogs", ["katze"] = "cat",
            ["katzen"] = "cats", ["tier"] = "animal", ["tiere"] = "animals", ["vogel"] = "bird",
            ["personen"] = "people", ["leute"] = "people", ["mann"] = "man", ["frau"] = "woman",
            ["jemand"] = "someone", ["kind"] = "child", ["paket"] = "package",
            ["pakete"] = "packages", ["lieferung"] = "delivery", ["klingel"] = "doorbell",
            ["türklingel"] = "doorbell", ["besucher"] = "visitor", ["bewegung"] = "motion",
            ["verdächtig"] = "suspicious", ["gefährlich"] = "dangerous",
            ["montag"] = "monday", ["dienstag"] = "tuesday", ["mittwoch"] = "wednesday",
            ["donnerstag"] = "thursday", ["freitag"] = "friday", ["samstag"] = "saturday",
            ["sonnabend"] = "saturday", ["sonntag"] = "sunday",
            ["januar"] = "january", ["februar"] = "february", ["märz"] = "march",
            ["juni"] = "june", ["juli"] = "july", ["oktober"] = "october", ["dezember"] = "december",
            ["zwei"] = "two", ["drei"] = "three", ["vier"] = "four", ["fünf"] = "five",
            ["sechs"] = "six", ["sieben"] = "seven", ["acht"] = "eight", ["neun"] = "nine",
            ["zehn"] = "ten", ["einer"] = "a", ["einem"] = "a",
        },
        ["es"] = new(StringComparer.Ordinal)
        {
            ["ayer"] = "yesterday", ["hoy"] = "today", ["anoche"] = "last night",
            ["anteayer"] = "day before yesterday", ["mañana"] = "morning", ["tarde"] = "afternoon",
            ["noche"] = "night", ["mediodía"] = "noon", ["medianoche"] = "midnight",
            ["última"] = "last", ["último"] = "last", ["últimas"] = "last", ["últimos"] = "last",
            ["pasada"] = "last", ["pasado"] = "last", ["semana"] = "week", ["semanas"] = "weeks",
            ["mes"] = "month", ["meses"] = "months", ["día"] = "day", ["días"] = "days",
            ["hora"] = "hour", ["horas"] = "hours", ["minutos"] = "minutes", ["desde"] = "since",
            ["hasta"] = "until", ["antes"] = "before", ["después"] = "after", ["entre"] = "between",
            ["y"] = "and", ["o"] = "or", ["u"] = "or", ["sin"] = "without", ["ningún"] = "no",
            ["ninguna"] = "no", ["el"] = "the", ["la"] = "the", ["los"] = "the", ["las"] = "the",
            ["del"] = "the", ["al"] = "at", ["un"] = "a", ["una"] = "a", ["este"] = "this",
            ["esta"] = "this", ["en"] = "in", ["a"] = "at", ["por"] = "in",
            ["de"] = "of", ["durante"] = "during", ["animal"] = "animal",
            ["coche"] = "car", ["coches"] = "cars", ["carro"] = "car", ["carros"] = "cars",
            ["auto"] = "car", ["vehículo"] = "vehicle", ["vehículos"] = "vehicles",
            ["camión"] = "truck", ["camioneta"] = "truck", ["moto"] = "motorcycle",
            ["perro"] = "dog", ["perros"] = "dogs", ["gato"] = "cat", ["gatos"] = "cats",
            ["animales"] = "animals", ["pájaro"] = "bird", ["persona"] = "person",
            ["personas"] = "people", ["gente"] = "people", ["hombre"] = "man", ["mujer"] = "woman",
            ["alguien"] = "someone", ["niño"] = "child", ["niña"] = "child",
            ["paquete"] = "package", ["paquetes"] = "packages", ["entrega"] = "delivery",
            ["timbre"] = "doorbell", ["visitante"] = "visitor", ["movimiento"] = "motion",
            ["sospechoso"] = "suspicious", ["peligroso"] = "dangerous",
            ["lunes"] = "monday", ["martes"] = "tuesday", ["miércoles"] = "wednesday",
            ["jueves"] = "thursday", ["viernes"] = "friday", ["sábado"] = "saturday",
            ["domingo"] = "sunday",
            ["enero"] = "january", ["febrero"] = "february", ["marzo"] = "march",
            ["abril"] = "april", ["mayo"] = "may", ["junio"] = "june", ["julio"] = "july",
            ["agosto"] = "august", ["septiembre"] = "september", ["octubre"] = "october",
            ["noviembre"] = "november", ["diciembre"] = "december",
            ["dos"] = "two", ["tres"] = "three", ["cuatro"] = "four", ["cinco"] = "five",
            ["seis"] = "six", ["siete"] = "seven", ["ocho"] = "eight", ["nueve"] = "nine",
            ["diez"] = "ten",
        },
        ["nl"] = new(StringComparer.Ordinal)
        {
            ["gisteren"] = "yesterday", ["vandaag"] = "today", ["eergisteren"] = "day before yesterday",
            ["vanavond"] = "tonight", ["vannacht"] = "tonight", ["vanmorgen"] = "this morning",
            ["vanochtend"] = "this morning", ["vanmiddag"] = "this afternoon",
            ["ochtend"] = "morning", ["middag"] = "afternoon", ["avond"] = "evening",
            ["nacht"] = "night", ["laatste"] = "last", ["vorige"] = "last", ["afgelopen"] = "past",
            ["week"] = "week", ["weken"] = "weeks", ["maand"] = "month", ["maanden"] = "months", ["dag"] = "day",
            ["dagen"] = "days", ["uur"] = "hour", ["uren"] = "hours", ["minuten"] = "minutes",
            ["sinds"] = "since", ["tot"] = "until", ["voor"] = "before", ["na"] = "after",
            ["tussen"] = "between", ["van"] = "from", ["om"] = "at", ["rond"] = "around",
            ["en"] = "and", ["of"] = "or", ["zonder"] = "without", ["geen"] = "no",
            ["niet"] = "not", ["de"] = "the", ["het"] = "the", ["een"] = "a", ["deze"] = "this",
            ["dit"] = "this", ["geleden"] = "ago", ["op"] = "on", ["tijdens"] = "during",
            ["auto"] = "car", ["auto's"] = "cars", ["autos"] = "cars", ["voertuig"] = "vehicle",
            ["voertuigen"] = "vehicles", ["vrachtwagen"] = "truck", ["hond"] = "dog",
            ["honden"] = "dogs", ["kat"] = "cat", ["katten"] = "cats", ["dier"] = "animal",
            ["dieren"] = "animals", ["vogel"] = "bird", ["persoon"] = "person",
            ["personen"] = "people", ["mensen"] = "people", ["iemand"] = "someone",
            ["vrouw"] = "woman", ["kind"] = "child", ["pakket"] = "package",
            ["pakketten"] = "packages", ["bezorging"] = "delivery", ["deurbel"] = "doorbell",
            ["bezoeker"] = "visitor", ["beweging"] = "motion", ["verdacht"] = "suspicious",
            ["gevaarlijk"] = "dangerous",
            ["maandag"] = "monday", ["dinsdag"] = "tuesday", ["woensdag"] = "wednesday",
            ["donderdag"] = "thursday", ["vrijdag"] = "friday", ["zaterdag"] = "saturday",
            ["zondag"] = "sunday",
            ["januari"] = "january", ["februari"] = "february", ["maart"] = "march",
            ["mei"] = "may", ["juni"] = "june", ["juli"] = "july", ["augustus"] = "august",
            ["oktober"] = "october",
            ["één"] = "one", ["twee"] = "two", ["drie"] = "three", ["vier"] = "four",
            ["vijf"] = "five", ["zes"] = "six", ["zeven"] = "seven", ["negen"] = "nine",
            ["tien"] = "ten",
        },
        ["pt"] = new(StringComparer.Ordinal)
        {
            ["ontem"] = "yesterday", ["hoje"] = "today", ["anteontem"] = "day before yesterday",
            ["manhã"] = "morning", ["tarde"] = "afternoon", ["noite"] = "night",
            ["meio-dia"] = "noon", ["meia-noite"] = "midnight", ["última"] = "last",
            ["último"] = "last", ["últimas"] = "last", ["últimos"] = "last", ["passada"] = "last",
            ["passado"] = "last", ["semana"] = "week", ["semanas"] = "weeks", ["mês"] = "month",
            ["meses"] = "months", ["dia"] = "day", ["dias"] = "days", ["hora"] = "hour",
            ["horas"] = "hours", ["minutos"] = "minutes", ["desde"] = "since", ["até"] = "until",
            ["antes"] = "before", ["depois"] = "after", ["entre"] = "between", ["e"] = "and",
            ["ou"] = "or", ["sem"] = "without", ["nenhum"] = "no", ["nenhuma"] = "no",
            ["não"] = "not", ["o"] = "the", ["a"] = "the", ["os"] = "the", ["as"] = "the",
            ["do"] = "the", ["da"] = "the", ["dos"] = "the", ["das"] = "the", ["um"] = "a",
            ["uma"] = "a", ["este"] = "this", ["esta"] = "this", ["em"] = "in", ["no"] = "in",
            ["na"] = "in", ["nos"] = "in", ["nas"] = "in", ["às"] = "at", ["ao"] = "at",
            ["pela"] = "in", ["pelo"] = "in", ["de"] = "of", ["durante"] = "during",
            ["carro"] = "car", ["carros"] = "cars", ["veículo"] = "vehicle",
            ["veículos"] = "vehicles", ["caminhão"] = "truck", ["camião"] = "truck",
            ["moto"] = "motorcycle", ["cachorro"] = "dog", ["cachorros"] = "dogs",
            ["cão"] = "dog", ["cães"] = "dogs", ["gato"] = "cat", ["gatos"] = "cats",
            ["animais"] = "animals", ["pássaro"] = "bird", ["pessoa"] = "person",
            ["pessoas"] = "people", ["homem"] = "man", ["mulher"] = "woman",
            ["alguém"] = "someone", ["criança"] = "child", ["pacote"] = "package",
            ["encomenda"] = "package", ["entrega"] = "delivery", ["campainha"] = "doorbell",
            ["visitante"] = "visitor", ["movimento"] = "motion", ["suspeito"] = "suspicious",
            ["perigoso"] = "dangerous",
            ["segunda"] = "monday", ["segunda-feira"] = "monday", ["terça"] = "tuesday",
            ["terça-feira"] = "tuesday", ["quarta"] = "wednesday", ["quarta-feira"] = "wednesday",
            ["quinta"] = "thursday", ["quinta-feira"] = "thursday", ["sexta"] = "friday",
            ["sexta-feira"] = "friday", ["sábado"] = "saturday", ["domingo"] = "sunday",
            ["janeiro"] = "january", ["fevereiro"] = "february", ["março"] = "march",
            ["abril"] = "april", ["maio"] = "may", ["junho"] = "june", ["julho"] = "july",
            ["agosto"] = "august", ["setembro"] = "september", ["outubro"] = "october",
            ["novembro"] = "november", ["dezembro"] = "december",
            ["dois"] = "two", ["duas"] = "two", ["três"] = "three", ["quatro"] = "four",
            ["cinco"] = "five", ["seis"] = "six", ["sete"] = "seven", ["oito"] = "eight",
            ["nove"] = "nine", ["dez"] = "ten",
        },
        ["pl"] = new(StringComparer.Ordinal)
        {
            ["wczoraj"] = "yesterday", ["dzisiaj"] = "today", ["dziś"] = "today",
            ["przedwczoraj"] = "day before yesterday", ["rano"] = "morning",
            ["popołudnie"] = "afternoon", ["popołudniu"] = "afternoon", ["wieczór"] = "evening",
            ["wieczorem"] = "evening", ["noc"] = "night", ["nocy"] = "night",
            ["południe"] = "noon", ["północ"] = "midnight", ["ostatni"] = "last",
            ["ostatnia"] = "last", ["ostatnie"] = "last", ["ostatnich"] = "last",
            ["ostatnim"] = "last", ["zeszły"] = "last", ["zeszła"] = "last", ["zeszłym"] = "last",
            ["zeszłej"] = "last", ["tydzień"] = "week", ["tygodnia"] = "week",
            ["tygodniu"] = "week", ["tygodnie"] = "weeks", ["tygodni"] = "weeks",
            ["miesiąc"] = "month", ["miesiąca"] = "month", ["miesiącu"] = "month",
            ["miesiące"] = "months", ["miesięcy"] = "months", ["dzień"] = "day", ["dni"] = "days",
            ["dniu"] = "day", ["godzina"] = "hour", ["godziny"] = "hours", ["godzin"] = "hours",
            ["godzinę"] = "hour", ["minut"] = "minutes", ["od"] = "since", ["do"] = "until",
            ["przed"] = "before", ["po"] = "after", ["między"] = "between",
            ["pomiędzy"] = "between", ["i"] = "and", ["oraz"] = "and", ["lub"] = "or",
            ["albo"] = "or", ["bez"] = "without", ["żaden"] = "no", ["żadnych"] = "no",
            ["nie"] = "not", ["w"] = "in", ["temu"] = "ago", ["ten"] = "this", ["ta"] = "this",
            ["podczas"] = "during",
            ["samochód"] = "car", ["samochody"] = "cars", ["samochodów"] = "cars",
            ["auto"] = "car", ["pojazd"] = "vehicle", ["pojazdy"] = "vehicles",
            ["ciężarówka"] = "truck", ["motocykl"] = "motorcycle", ["pies"] = "dog",
            ["psy"] = "dogs", ["psów"] = "dogs", ["psa"] = "dog", ["kot"] = "cat",
            ["koty"] = "cats", ["kota"] = "cat", ["zwierzę"] = "animal",
            ["zwierzęta"] = "animals", ["ptak"] = "bird", ["osoba"] = "person",
            ["osoby"] = "people", ["ludzie"] = "people", ["ktoś"] = "someone",
            ["mężczyzna"] = "man", ["kobieta"] = "woman", ["dziecko"] = "child",
            ["paczka"] = "package", ["paczki"] = "packages", ["przesyłka"] = "package",
            ["dostawa"] = "delivery", ["dzwonek"] = "doorbell", ["gość"] = "visitor",
            ["ruch"] = "motion", ["podejrzany"] = "suspicious", ["niebezpieczny"] = "dangerous",
            ["poniedziałek"] = "monday", ["wtorek"] = "tuesday", ["środa"] = "wednesday",
            ["środę"] = "wednesday", ["czwartek"] = "thursday", ["piątek"] = "friday",
            ["sobota"] = "saturday", ["sobotę"] = "saturday", ["niedziela"] = "sunday",
            ["niedzielę"] = "sunday",
            ["styczeń"] = "january", ["stycznia"] = "january", ["luty"] = "february",
            ["lutego"] = "february", ["marzec"] = "march", ["marca"] = "march",
            ["kwiecień"] = "april", ["kwietnia"] = "april", ["maja"] = "may",
            ["czerwiec"] = "june", ["czerwca"] = "june", ["lipiec"] = "july", ["lipca"] = "july",
            ["sierpień"] = "august", ["sierpnia"] = "august", ["wrzesień"] = "september",
            ["września"] = "september", ["październik"] = "october",
            ["października"] = "october", ["listopad"] = "november", ["listopada"] = "november",
            ["grudzień"] = "december", ["grudnia"] = "december",
            ["jeden"] = "one", ["dwa"] = "two", ["dwie"] = "two", ["trzy"] = "three",
            ["cztery"] = "four", ["pięć"] = "five", ["sześć"] = "six", ["siedem"] = "seven",
            ["osiem"] = "eight", ["dziewięć"] = "nine", ["dziesięć"] = "ten",
        },
    };
}
