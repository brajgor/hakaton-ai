using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VaiDrikstu.Domain.Assistant;

/// <summary>
/// Deterministic question answering over the open-data knowledge base. No LLM:
/// answers are built only from dictionaries, regulation texts and the pre-check result,
/// so every statement can be traced back to a source.
/// </summary>
public sealed class AssistantEngine(KnowledgeBase knowledgeBase)
{
    private const int MaxItems = 6;

    private static readonly Regex CadastreNumber = new(@"(?<!\d)(\d{11}|\d{13}|\d{14})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex EncumbranceNumber = new(@"(?<!\d)(\d{10}|\d{6})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex ShortNumber = new(@"(?<![\d.])(\d{1,5})(?![\d.]|\s?(?:m|km|ha|stāv|metr|м))", RegexOptions.Compiled);
    private static readonly Regex WordToken = new(@"[\p{L}_]+[\p{L}\d_\-]*\d*|AZ-\d+", RegexOptions.Compiled);
    private static readonly Regex ZoneCode = new(@"^(dz[smd]|jc|da|tr|ta|u|ū|tin|lm|ap|tdz|p[aiksrpvo]?)\d{0,2}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NatureSiteCode = new(@"^LV\d{7}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] HelpWords = ["help", "palidz", "palīdz", "помощ", "помоги", "sveiki", "labdien", "привет", "здравствуй", "hello", "hi"];
    private static readonly string[] MarkWords = ["atzim", "atzīm", "метк", "отметк", "mark"];
    private static readonly string[] MonumentWords = ["piemin", "памятн", "памятник", "monument", "nkmp"];
    private static readonly string[] ZoneWords = ["zon", "зон", "teritorij", "территор", "indeks", "индекс"];
    private static readonly string[] NextWords = ["talak", "jadara", "nakam", "soli", "solis", "griezties", "дальше", "делать", "следующ", "шаг", "обратиться", "next", "steps", "what should", "who should", "where do i"];
    private static readonly string[] StrongExplainWords =
    [
        "kapec", "rezultat", "paskaidro", "izskaidro",
        "почему", "результат", "объясни", "поясни",
        "why", "result", "explain",
    ];

    // Weak words only count when the question has no other content ("ko tas nozīmē?", "почему жёлтый?").
    private static readonly string[] WeakExplainWords =
    [
        "nozime", "dzelten", "zals", "zala", "sarkan", "krasa", "kods",
        "значит", "означа", "желт", "зелен", "красн", "цвет", "код",
        "mean", "yellow", "green", "red", "colour", "color", "code",
    ];

    public KnowledgeBase KnowledgeBase { get; } = knowledgeBase;

    public AssistantAnswer Answer(string? question, AssistantContext? context = null, string? preferredLanguage = null)
    {
        var text = (question ?? string.Empty).Trim();
        if (text.Length > 500)
        {
            text = text[..500];
        }

        var language = text.Any(char.IsLetter)
            ? TextAnalysis.DetectLanguage(text)
            : Normalize(preferredLanguage);
        var folded = TextAnalysis.Fold(text);
        var suggestions = Suggestions(language, context, folded);

        if (text.Length == 0 || IsHelp(folded))
        {
            return new AssistantAnswer(language, AssistantTexts.Get(language, "help"), [], suggestions, AssistantTexts.Get(language, "disclaimer"), true);
        }

        var items = new List<AssistantItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(AssistantItem item)
        {
            if (items.Count < MaxItems && seen.Add($"{item.Kind}|{item.Code}|{item.Title}"))
            {
                items.Add(item);
            }
        }

        // 1. Explicit codes in the question --------------------------------------------------
        foreach (var item in CodeItems(text, folded, language))
        {
            Add(item);
        }

        if (items.Count > 0)
        {
            return Build(language, "codes", items, suggestions);
        }

        // 2. Questions about the pre-check result ---------------------------------------------
        var wantsNext = ContainsAny(folded, NextWords);
        var contentTerms = TextAnalysis.Tokenize(text)
            .Where(token => !TextAnalysis.IsStopWord(token) && !WeakExplainWords.Any(word => token.StartsWith(word, StringComparison.Ordinal)))
            .ToList();
        var wantsExplain = ContainsAny(folded, StrongExplainWords)
            || (contentTerms.Count == 0 && ContainsAny(folded, WeakExplainWords));
        if (wantsNext || wantsExplain)
        {
            if (context is null)
            {
                var noContext = SearchItems(text, folded, language, null);
                return noContext.Count > 0
                    ? Build(language, "search", noContext, suggestions)
                    : new AssistantAnswer(language, AssistantTexts.Get(language, "noContext"), [], suggestions, AssistantTexts.Get(language, "disclaimer"), false);
            }

            if (wantsNext && !wantsExplain)
            {
                Add(NextStepsItem(context, language));
                return Build(language, "nextSteps", items, suggestions);
            }

            foreach (var item in ExplainResult(context, language))
            {
                Add(item);
            }

            if (wantsNext)
            {
                items.Add(NextStepsItem(context, language));
            }

            return Build(language, "explainResult", items, suggestions);
        }

        // 3. Free-text search over dictionaries, legislation and datasets -----------------------
        foreach (var item in SearchItems(text, folded, language, context))
        {
            Add(item);
        }

        return items.Count > 0
            ? Build(language, "search", items, suggestions)
            : new AssistantAnswer(language, AssistantTexts.Get(language, "notFound"), [], suggestions, AssistantTexts.Get(language, "disclaimer"), false);
    }

    // ------------------------------------------------------------------------------------------
    // Codes
    // ------------------------------------------------------------------------------------------

    private IEnumerable<AssistantItem> CodeItems(string text, string folded, string language)
    {
        var consumed = new List<(int Start, int End)>();

        foreach (Match match in CadastreNumber.Matches(text))
        {
            consumed.Add((match.Index, match.Index + match.Length));
            foreach (var item in CadastreItems(match.Value, language))
            {
                yield return item;
            }
        }

        foreach (Match match in EncumbranceNumber.Matches(text))
        {
            consumed.Add((match.Index, match.Index + match.Length));
            var label = KnowledgeBase.Find("encumbrance", match.Value);
            if (label is not null)
            {
                yield return FromLabel(label, language);
            }
        }

        var wantsMark = ContainsAny(folded, MarkWords);
        var wantsMonument = ContainsAny(folded, MonumentWords);
        if (wantsMark || wantsMonument)
        {
            foreach (Match match in ShortNumber.Matches(text))
            {
                if (consumed.Any(range => match.Index >= range.Start && match.Index < range.End))
                {
                    continue;
                }

                if (wantsMark)
                {
                    var code = match.Value.Length is 1 or 3 ? "0" + match.Value : match.Value;
                    var mark = KnowledgeBase.Find("mark", code);
                    if (mark is not null)
                    {
                        yield return FromLabel(mark, language);
                        continue;
                    }
                }

                if (wantsMonument && KnowledgeBase.MonumentByNumber(match.Value) is { } monument)
                {
                    yield return FromMonument(monument, language);
                }
            }
        }

        var tokens = WordToken.Matches(text).Select(match => match.Value).ToList();
        var shortQuestion = tokens.Count <= 4;
        var mentionsZone = ContainsAny(folded, ZoneWords);
        foreach (var token in tokens)
        {
            foreach (var label in LabelsForToken(token, shortQuestion || mentionsZone))
            {
                yield return FromLabel(label, language);
            }
        }
    }

    private IEnumerable<KnowledgeLabel> LabelsForToken(string token, bool allowSingleLetter)
    {
        var upper = token.ToUpperInvariant();
        var isUpper = token == upper && token.Any(char.IsLetter);

        if (token.Contains('_') || token.StartsWith("AZ-", StringComparison.OrdinalIgnoreCase) || NatureSiteCode.IsMatch(token))
        {
            return KnowledgeBase.FindByCode(token);
        }

        if (ZoneCode.IsMatch(token) && (token.Any(char.IsDigit) || isUpper || (allowSingleLetter && char.IsUpper(token[0]))))
        {
            var normalized = token.StartsWith('u') || token.StartsWith('U') ? "Ū" + token[1..] : token;
            var found = KnowledgeBase.FindByCode(normalized);
            if (found.Count > 0)
            {
                return found.Take(3);
            }
        }

        if (token.Length == 1)
        {
            return allowSingleLetter && isUpper ? KnowledgeBase.FindByCode(token).Take(3) : [];
        }

        // Upper-case abbreviations such as DLZ, AAA, STATUSS, TIPS_CD, KODS
        return isUpper && token.Length >= 2 ? KnowledgeBase.FindByCode(token).Take(3) : [];
    }

    private IEnumerable<AssistantItem> CadastreItems(string cadastre, string language)
    {
        var found = false;
        if (KnowledgeBase.CadastreMarks.TryGetValue(cadastre, out var marks))
        {
            found = true;
            var details = marks.M
                .Where(mark => mark.Length > 0 && mark[0] is not null)
                .Select(mark =>
                {
                    var label = KnowledgeBase.Find("mark", mark[0]!);
                    var title = label is null ? mark[0] : $"{mark[0]} - {label.Title}";
                    return mark.Length > 1 && mark[1] is not null
                        ? $"{title} ({AssistantTexts.Get(language, "marks.date", mark[1]!)})"
                        : title!;
                })
                .ToArray();
            yield return new AssistantItem(
                "cadastre",
                AssistantTexts.Get(language, "kind.cadastre"),
                cadastre,
                AssistantTexts.Get(language, "marks.title", cadastre),
                AssistantTexts.Get(language, "detail.objectType", marks.T),
                "VZD Kadastra informācijas sistēma: atzīmes",
                null,
                details,
                false);
        }

        foreach (var monument in KnowledgeBase.MonumentsByCadastre(cadastre).Take(3))
        {
            found = true;
            yield return FromMonument(monument, language);
        }

        if (!found)
        {
            yield return new AssistantItem(
                "cadastre",
                AssistantTexts.Get(language, "kind.cadastre"),
                cadastre,
                cadastre,
                AssistantTexts.Get(language, "cadastreNotFound", cadastre),
                "VZD Kadastra informācijas sistēma",
                null,
                [],
                false);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Pre-check result
    // ------------------------------------------------------------------------------------------

    private IEnumerable<AssistantItem> ExplainResult(AssistantContext context, string language)
    {
        if (KnowledgeBase.Find("level", context.Level) is { } level)
        {
            yield return FromLabel(level, language);
        }

        foreach (var code in context.ReasonCodes.Where(code => code != ReasonCodes.AddressFound))
        {
            if (KnowledgeBase.Find("reason", code) is { } reason)
            {
                yield return FromLabel(reason, language);
            }
        }

        foreach (var finding in context.Findings)
        {
            var details = new List<string>
            {
                finding.Intersects
                    ? AssistantTexts.Get(language, "finding.intersects")
                    : AssistantTexts.Get(language, "finding.distance", Math.Round(finding.DistanceMeters ?? 0, 0).ToString(CultureInfo.InvariantCulture)),
                AssistantTexts.Get(language, "finding.code", finding.Code),
            };
            yield return new AssistantItem(
                "finding",
                AssistantTexts.Get(language, "kind.finding"),
                finding.Code,
                finding.Name,
                finding.Category,
                FindingSource(finding.Source),
                null,
                details.ToArray(),
                language != "lv");
        }
    }

    private static string FindingSource(string source) => source switch
    {
        "heritage" => "NKMP kultūras mantojuma dati",
        "heritage_protection_zone" or "urban_heritage_monument" or "cultural_historic_territory" or "building_protection_area" => "Rīgas aizsargjoslu un kultūrvēsturisko teritoriju slāņi",
        "nature" or "protected_nature_area" => "DAP dabas datu slānis",
        "red_line" => "Rīgas ielu sarkanās līnijas",
        "flood_risk" => "Applūšanas riska teritorijas",
        "planning_zone" => "Teritorijas plānojuma funkcionālais zonējums",
        _ => source,
    };

    private static AssistantItem NextStepsItem(AssistantContext context, string language) =>
        new(
            "steps",
            AssistantTexts.Get(language, "kind.steps"),
            context.Level,
            AssistantTexts.Get(language, "kind.steps"),
            string.Empty,
            "Vai drīkstu te darīt? noteikumu dzinējs",
            null,
            context.NextSteps.ToArray(),
            language != "lv");

    // ------------------------------------------------------------------------------------------
    // Free-text search
    // ------------------------------------------------------------------------------------------

    private List<AssistantItem> SearchItems(string text, string folded, string language, AssistantContext? context)
    {
        var groups = TextAnalysis.Terms(text)
            .Distinct(StringComparer.Ordinal)
            .Select(term => Synonyms.Expand([term]))
            .ToList();
        if (groups.Count == 0)
        {
            return [];
        }

        var query = groups.SelectMany(group => group).Distinct(StringComparer.Ordinal).ToList();
        var required = groups.Count <= 2 ? groups.Count : (int)Math.Ceiling(groups.Count * 0.6);
        var wantsMonument = ContainsAny(folded, MonumentWords);
        var heritageContext = context?.Findings.Any(finding => finding.Source.Contains("heritage") || finding.Source is "cultural_historic_territory" or "building_protection_area") == true;
        var natureContext = context?.Findings.Any(finding => finding.Source is "nature" or "protected_nature_area") == true;

        var ranked = KnowledgeBase.Index.Search(query, 60)
            .Select(hit =>
            {
                var coverage = groups.Count(group => group.Any(hit.Matched.Contains));
                var prior = hit.Item.Kind switch
                {
                    "monument" => wantsMonument ? 1.3 : 0.5,
                    "regulation" => 1.0,
                    "dataset" => 0.9,
                    "address-column" => 0.6,
                    _ => 1.25,
                };
                var source = hit.Item.Document?.Source ?? hit.Item.Label?.Source ?? string.Empty;
                if (heritageContext && source.Contains("kultūras", StringComparison.OrdinalIgnoreCase))
                {
                    prior *= 1.2;
                }

                if (natureContext && source.Contains("dabas", StringComparison.OrdinalIgnoreCase))
                {
                    prior *= 1.2;
                }

                return (hit, coverage, score: hit.Score * prior * (0.5 + 0.5 * coverage / (double)groups.Count));
            })
            .Where(entry => entry.coverage >= required)
            .OrderByDescending(entry => entry.score)
            .Take(4)
            .ToList();

        return ranked
            .Select(entry => FromEntry(entry.hit.Item, entry.hit.Matched, language))
            .ToList();
    }

    private AssistantItem FromEntry(KnowledgeEntry entry, IReadOnlySet<string> matched, string language)
    {
        if (entry.Label is not null)
        {
            return FromLabel(entry.Label, language);
        }

        if (entry.Monument is not null)
        {
            return FromMonument(entry.Monument, language);
        }

        var document = entry.Document!;
        return new AssistantItem(
            document.Kind,
            AssistantTexts.Get(language, $"kind.{document.Kind}"),
            document.Ref,
            document.Title,
            Snippet(document.Text, matched),
            document.Source,
            document.Url,
            [],
            language != "lv");
    }

    // ------------------------------------------------------------------------------------------
    // Rendering helpers
    // ------------------------------------------------------------------------------------------

    private AssistantItem FromLabel(KnowledgeLabel label, string language)
    {
        LabelTranslation? translation = null;
        if (language != "lv" && label.Translations is not null && label.Translations.TryGetValue(language, out var found))
        {
            translation = found;
        }

        var details = new List<string>();
        if (translation is not null && label.Kind is not ("reason" or "level"))
        {
            details.Add($"LV: {label.Text}");
        }

        details.AddRange(MetaDetails(label, language));

        return new AssistantItem(
            label.Kind,
            AssistantTexts.Get(language, $"kind.{label.Kind}"),
            label.Code,
            translation?.Title ?? label.Title,
            translation?.Text ?? (label.Text == label.Title ? string.Empty : label.Text),
            label.Source,
            label.Url,
            details.ToArray(),
            language != "lv" && translation is null);
    }

    private IEnumerable<string> MetaDetails(KnowledgeLabel label, string language)
    {
        if (label.Meta is null)
        {
            yield break;
        }

        if (label.Meta.TryGetValue("group", out var group) && group.ValueKind == JsonValueKind.String)
        {
            yield return AssistantTexts.Get(language, "detail.group", group.GetString()!);
        }

        if (label.Meta.TryGetValue("maxFloors", out var floors) && floors.ValueKind == JsonValueKind.String)
        {
            yield return AssistantTexts.Get(language, "detail.floors", floors.GetString()!);
        }

        if (label.Kind is "encumbrance" or "mark" && label.Meta.TryGetValue("count", out var count) && count.ValueKind == JsonValueKind.Number && count.GetInt32() > 0)
        {
            yield return AssistantTexts.Get(language, "detail.count", count.GetInt32().ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' '));
        }

        if (label.Meta.TryGetValue("regions", out var regions) && regions.ValueKind == JsonValueKind.Object)
        {
            var names = regions.EnumerateObject().Select(region => region.Name).ToArray();
            if (names.Length > 0)
            {
                yield return AssistantTexts.Get(language, "detail.regions", string.Join(", ", names));
            }
        }

        if (label.Meta.TryGetValue("legacy", out var legacy) && legacy.ValueKind == JsonValueKind.True)
        {
            yield return AssistantTexts.Get(language, "detail.legacy");
        }
    }

    private AssistantItem FromMonument(MonumentRecord monument, string language)
    {
        var details = new List<string>
        {
            AssistantTexts.Get(language, "monument.details", monument.Value, monument.Group, monument.Dating, monument.Condition),
        };
        if (monument.EncumbranceCode is { Length: > 0 } code)
        {
            var label = KnowledgeBase.Find("encumbrance", code);
            details.Add(label is null ? code : $"{code} - {label.Title}");
        }

        if (monument.Cadastre.Length > 0)
        {
            details.Add(string.Join(", ", monument.Cadastre.Take(5)));
        }

        return new AssistantItem(
            "monument",
            AssistantTexts.Get(language, "kind.monument"),
            monument.Nr,
            monument.Name,
            monument.Location,
            "NKMP: Valsts aizsargājamo nekustamo kultūras pieminekļu saraksts",
            null,
            details.ToArray(),
            language != "lv");
    }

    /// <summary>Cuts ~600 characters around the first matched term.</summary>
    public static string Snippet(string text, IEnumerable<string> matched, int length = 600)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var folded = TextAnalysis.Fold(text);
        var position = matched
            .Where(term => term.Length >= 3 && !term.All(char.IsDigit))
            .Select(term => folded.IndexOf(term, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        position = Math.Clamp(position, 0, text.Length - 1);

        var start = Math.Max(0, position - 150);
        if (start > 0)
        {
            var sentence = text.LastIndexOf(". ", position, Math.Min(position, 300), StringComparison.Ordinal);
            start = sentence > 0 ? sentence + 2 : start;
        }

        var end = Math.Min(text.Length, start + length);
        var snippet = text[start..end].Trim();
        return (start > 0 ? "… " : string.Empty) + snippet + (end < text.Length ? " …" : string.Empty);
    }

    private static AssistantAnswer Build(string language, string introKey, List<AssistantItem> items, string[] suggestions) =>
        new(language, AssistantTexts.Get(language, introKey), items.ToArray(), suggestions, AssistantTexts.Get(language, "disclaimer"), items.Count > 0);

    private static string[] Suggestions(string language, AssistantContext? context, string foldedQuestion)
    {
        var list = new List<string>();
        if (context is not null)
        {
            list.Add(AssistantTexts.Get(language, "suggest.why"));
            list.Add(AssistantTexts.Get(language, "suggest.next"));
            list.AddRange(context.ReasonCodes
                .Where(code => code is not ReasonCodes.AddressFound and not ReasonCodes.OfficialReviewRequired)
                .Take(2)
                .Select(code => AssistantTexts.Get(language, "suggest.code", code)));
        }

        list.Add(AssistantTexts.Get(language, "suggest.encumbrance"));
        list.Add(AssistantTexts.Get(language, "suggest.zone"));
        list.Add(AssistantTexts.Get(language, "suggest.protection"));
        list.Add(AssistantTexts.Get(language, "suggest.mark"));

        return list
            .Where(item => TextAnalysis.Fold(item) != foldedQuestion)
            .Distinct()
            .Take(4)
            .ToArray();
    }

    private static bool IsHelp(string folded)
    {
        var tokens = TextAnalysis.Tokenize(folded).ToList();
        return tokens.Count <= 3 && tokens.Any(token => HelpWords.Any(word =>
        {
            var help = TextAnalysis.Fold(word);
            return token == help || (help.Length >= 4 && token.StartsWith(help, StringComparison.Ordinal));
        }));
    }

    /// <summary>True when a token starts with one of the (folded) words; phrases with spaces match as substrings.</summary>
    private static bool ContainsAny(string folded, IEnumerable<string> words)
    {
        var tokens = TextAnalysis.Tokenize(folded).ToList();
        return words.Select(TextAnalysis.Fold).Any(word => word.Contains(' ')
            ? folded.Contains(word, StringComparison.Ordinal)
            : tokens.Any(token => token.StartsWith(word, StringComparison.Ordinal)));
    }

    private static string Normalize(string? language) =>
        language is not null && AssistantTexts.Languages.Contains(language) ? language : "lv";
}
