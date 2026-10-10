using System.Globalization;
using System.Text;

namespace VaiDrikstu.Domain.Assistant;

/// <summary>
/// Language-agnostic text helpers: diacritics folding, tokenizing and a very small
/// suffix stemmer that is good enough for Latvian, Russian and English keywords.
/// </summary>
public static class TextAnalysis
{
    private static readonly string[] Endings =
    [
        // Russian (after folding й→и, ё→е)
        "ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими", "иях", "ах", "ях", "ои", "еи", "ии", "ыи",
        "ая", "яя", "ое", "ее", "ые", "ие", "ов", "ев", "ам", "ям", "ом", "ем", "ую", "юю", "ия",
        "а", "я", "о", "е", "ы", "и", "у", "ю", "ь",
        // Latvian (diacritics already folded)
        "iem", "ajam", "ajai", "ais", "aja", "am", "em", "os", "as", "es", "is", "us", "um", "ai", "ei", "ie",
        // English
        "ing", "ies", "ed", "es",
        "a", "e", "i", "u", "s", "o",
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // lv
        "ka", "kas", "ko", "kur", "kad", "kā", "ka", "vai", "un", "ir", "nav", "par", "pie", "no", "uz", "ar", "lai", "tas", "ta",
        "šis", "sis", "šī", "si", "tā", "to", "tie", "es", "man", "mans", "mana", "mani", "jūs", "jus", "lūdzu", "ludzu", "šo", "so",
        "nozīmē", "nozime", "kāpēc", "kapec", "kādi", "kadi", "kāds", "kads", "kāda", "kada", "vēl", "vel", "arī", "ari", "jā", "ja",
        "drīkst", "drikst", "var", "būs", "bus", "šeit", "seit",
        // ru
        "что", "это", "как", "где", "когда", "или", "и", "в", "во", "на", "по", "с", "со", "к", "ко", "о", "об", "от", "до", "за",
        "для", "у", "ли", "же", "мне", "мой", "моя", "мое", "мои", "я", "вы", "ты", "он", "она", "оно", "они", "такое", "значит",
        "почему", "какой", "какая", "какие", "каким", "можно", "нужно", "ли", "ну", "а", "но", "не", "да", "нет", "есть", "тут",
        "здесь", "пожалуйста", "расскажи", "скажи", "объясни",
        // en
        "the", "a", "an", "is", "are", "what", "does", "do", "mean", "means", "why", "how", "where", "when", "of", "in", "on",
        "at", "to", "for", "and", "or", "my", "me", "i", "it", "this", "that", "can", "please", "tell", "about", "explain",
    };

    /// <summary>Lower-cases and strips combining marks (ā→a, š→s, й→и, ё→е).</summary>
    public static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Splits on anything that is not a letter or digit; returns folded tokens.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var folded = Fold(text);
        var builder = new StringBuilder();
        foreach (var ch in folded)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    public static bool IsStopWord(string foldedToken) => StopWords.Contains(foldedToken);

    public static string Stem(string token)
    {
        if (token.Length <= 3 || token.All(char.IsDigit))
        {
            return token;
        }

        foreach (var ending in Endings)
        {
            if (token.Length - ending.Length >= 3 && token.EndsWith(ending, StringComparison.Ordinal))
            {
                token = token[..^ending.Length];
                break;
            }
        }

        return token.Length > 7 ? token[..7] : token;
    }

    /// <summary>Tokens for indexing/search: folded, stop words removed, stemmed.</summary>
    public static List<string> Terms(string text) =>
        Tokenize(text)
            .Where(token => !IsStopWord(token) && (token.Length > 1 || char.IsDigit(token[0])))
            .Select(Stem)
            .ToList();

    /// <summary>Very small heuristic detector for lv / ru / en.</summary>
    public static string DetectLanguage(string text)
    {
        // Codes (HERITAGE_SITE_NEARBY, DzS1, 7312030100) say nothing about the language of the question.
        var words = System.Text.RegularExpressions.Regex.Replace(text, @"[\p{Lu}\d_\-]{2,}(?![\p{Ll}])|\S*\d\S*", " ");
        var cyrillic = words.Count(ch => ch is >= 'Ѐ' and <= 'ӿ');
        var letters = words.Count(char.IsLetter);
        if (letters > 0 && cyrillic * 2 >= letters)
        {
            return "ru";
        }

        if (words.Any(ch => "āčēģīķļņšūžĀČĒĢĪĶĻŅŠŪŽ".Contains(ch)))
        {
            return "lv";
        }

        var tokens = Tokenize(words).ToList();
        string[] english = ["the", "what", "does", "is", "are", "mean", "why", "how", "which", "where", "can", "my", "this", "of", "zone", "code", "next", "do", "need", "permit"];
        string[] latvian = ["kas", "ko", "ir", "vai", "kur", "kapec", "nozime", "kods", "zona", "man", "jadara", "lidz", "tas", "par", "un", "drikst"];
        var en = tokens.Count(token => english.Contains(token));
        var lv = tokens.Count(token => latvian.Contains(token));
        return en > lv ? "en" : "lv";
    }
}

/// <summary>
/// Maps Russian/English (and colloquial Latvian) words to the Latvian vocabulary the
/// knowledge base is written in, so a Russian question can find Latvian texts.
/// </summary>
public static class Synonyms
{
    private static readonly (string[] From, string To)[] Raw =
    [
        (["охранная", "охранной", "охранные", "охранных", "охранную", "protection", "buffer"], "aizsargjosla"),
        (["зона", "зоны", "зоне", "зону", "зон", "zone", "zones", "zoning", "зонирование"], "zona teritorija zonējums"),
        (["памятник", "памятника", "памятники", "памятников", "monument", "monuments", "heritage", "наследие", "наследия"], "piemineklis kultūras mantojums"),
        (["обременение", "обременения", "обременений", "encumbrance", "encumbrances", "restriction", "ограничение", "ограничения"], "apgrūtinājums aprobežojums"),
        (["метка", "метки", "меток", "отметка", "отметки", "mark", "marks", "атзиме"], "atzīme"),
        (["кадастр", "кадастровый", "кадастрового", "cadastre", "cadastral"], "kadastrs apzīmējums"),
        (["участок", "участка", "участке", "parcel", "plot", "land"], "zemes vienība"),
        (["здание", "здания", "дом", "дома", "building", "house"], "ēka būve"),
        (["окна", "окно", "окон", "window", "windows"], "logi"),
        (["фасад", "фасада", "facade"], "fasāde"),
        (["солнечные", "солнечных", "solar"], "saules"),
        (["панели", "панелей", "panels", "panel"], "paneļi"),
        (["вывеска", "вывески", "вывеску", "sign", "signboard"], "izkārtne"),
        (["терраса", "террасу", "террасы", "terrace"], "terase"),
        (["сарай", "пристройка", "пристройку", "вспомогательное", "shed", "outbuilding"], "palīgēka"),
        (["гараж", "garage"], "garāža"),
        (["забор", "ограда", "fence"], "žogs"),
        (["строительство", "строить", "построить", "стройка", "construction", "build"], "būvniecība būvdarbi"),
        (["разрешение", "разрешения", "permit", "permission"], "atļauja"),
        (["согласование", "согласовать", "approval", "approve"], "saskaņošana saskaņot"),
        (["стройуправа", "стройуправление", "building board"], "būvvalde"),
        (["реконструкция", "перестройка", "reconstruction"], "pārbūve"),
        (["ремонт", "реставрация", "renovation", "repair"], "atjaunošana"),
        (["снос", "демонтаж", "demolition"], "nojaukšana"),
        (["этаж", "этажей", "этажа", "этажность", "floors", "storeys", "floor"], "stāvi"),
        (["высота", "height"], "augstums"),
        (["лес", "леса", "forest"], "mežs"),
        (["природа", "природы", "природная", "природной", "nature", "natural"], "dabas daba"),
        (["заповедник", "заказник", "reserve"], "liegums rezervāts"),
        (["парк", "парка", "park"], "parks"),
        (["вода", "водная", "водный", "water"], "ūdens"),
        (["река", "реки", "river"], "upe ūdenstece"),
        (["озеро", "озера", "lake"], "ezers"),
        (["море", "моря", "побережье", "sea", "coast"], "jūra piekraste"),
        (["дюна", "дюны", "dune"], "kāpa"),
        (["наводнение", "затопление", "паводок", "flood", "flooding"], "applūstoša applūšanas"),
        (["шум", "noise"], "troksnis trokšņa"),
        (["газ", "газопровод", "gas"], "gāze gāzesvads"),
        (["электросеть", "электрические", "электричество", "power", "electric", "electricity"], "elektrisko tīklu"),
        (["железная", "железной", "железнодорожный", "railway", "rail"], "dzelzceļš"),
        (["дорога", "дороги", "улица", "улицы", "road", "street"], "ceļš iela"),
        (["красная", "красные", "red"], "sarkanā"),
        (["линия", "линии", "line", "lines"], "līnija"),
        (["кладбище", "cemetery"], "kapsēta"),
        (["водопровод", "водоснабжение"], "ūdensvads"),
        (["канализация", "sewer", "sewage"], "kanalizācija"),
        (["дерево", "деревья", "tree", "trees"], "koki"),
        (["ипотека", "mortgage"], "hipotēka"),
        (["запрет", "запрещение", "prohibition"], "aizliegums"),
        (["отчуждение", "отчуждения"], "atsavināšana"),
        (["граница", "границы", "boundary", "border"], "robeža"),
        (["координаты", "coordinates"], "koordinātu"),
        (["адрес", "адреса", "address"], "adrese"),
        (["улица", "street"], "iela"),
        (["центр", "centre", "center"], "centra centrs"),
        (["исторический", "исторического", "historic", "historical"], "vēsturiskā"),
        (["жилая", "жилой", "жилые", "residential", "housing"], "dzīvojamās apbūves"),
        (["частный", "частные", "особняк", "detached"], "savrupmāju"),
        (["многоэтажная", "многоэтажные", "multistorey", "apartment"], "daudzstāvu"),
        (["промышленная", "промышленный", "industrial"], "rūpnieciskās"),
        (["данные", "набор", "dataset", "data"], "datu kopa"),
        (["закон", "law", "act"], "likums"),
        (["правила", "regulation", "regulations", "rules"], "noteikumi"),
        (["статья", "article"], "pants"),
        (["пункт", "point", "paragraph"], "punkts"),
        // colloquial Latvian
        (["logus", "logiem"], "logi"),
        (["paneli", "panelus"], "paneļi"),
    ];

    private static readonly Dictionary<string, List<string>> Map = Build();

    private static Dictionary<string, List<string>> Build()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (from, to) in Raw)
        {
            var targets = TextAnalysis.Terms(to);
            foreach (var word in from)
            {
                foreach (var key in TextAnalysis.Terms(word))
                {
                    if (!map.TryGetValue(key, out var list))
                    {
                        list = [];
                        map[key] = list;
                    }

                    list.AddRange(targets.Where(target => !list.Contains(target)));
                }
            }
        }

        return map;
    }

    /// <summary>Adds Latvian terms for every foreign term; original terms are kept.</summary>
    public static List<string> Expand(IEnumerable<string> terms)
    {
        var result = new List<string>();
        foreach (var term in terms)
        {
            if (!result.Contains(term))
            {
                result.Add(term);
            }

            if (Map.TryGetValue(term, out var extra))
            {
                result.AddRange(extra.Where(item => !result.Contains(item)));
            }
        }

        return result;
    }
}
