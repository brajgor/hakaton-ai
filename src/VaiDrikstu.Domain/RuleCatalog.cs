namespace VaiDrikstu.Domain;

/// <summary>
/// The reviewed, versioned MVP rule registry. Legal text is never interpreted at request time.
/// </summary>
public static class RuleCatalog
{
    private const string BuildingLawHash = "8a3f2cca6a9093040ea2d847c3eb375ae7325c0e440bc75ca8024239b6f7f9a2";
    private const string BuildingRulesHash = "632b80790e479543963cc3e052189ccaa32f645c849d487b3590ca935e0964a5";
    private const string GeneralBuildingRulesHash = "014daa9c79204bee4c5c9dafb8525dbb4e4e81752c835cb7d42d18d25bf41de7";
    private const string HeritageHash = "0d243ef68ab5bad34834326b21c172fa76118b059c6b7f3bc92bcf89d922d198";
    private const string NatureHash = "7ef5c1c2230d5d2e2cc2cb7c2d2662aec5bdb7ee1f3aacce13d7009f9b66efbe";
    private const string ProtectionZonesLawHash = "aa6a07a8dec7f66394ade97a1971ca2c8091eb3eafc8863fc67b1954ef643208";
    private const string ProtectionZonesHash = "16325d6b33524d241cd37fbee3aaf2cf4b26d1e9406ec04e9cb24a16ace5f7be";
    private const string RigaRulesHash = "a0e5a308b8ab2478ed71c524ff972a3543b963e2fe8b9d9c92fa42d4ea493588";
    private const string RigaRedLinesHash = "e9342d0fe18c89a521fd92917e079d3eac15296182c1160e66d8a5550e2e6651";

    public static readonly LegalSourceSnapshot[] SourceSnapshots =
    [
        new("building-law", "Būvniecības likums", "https://likumi.lv/ta/id/258572-buvniecibas-likums", BuildingLawHash),
        new("building-rules-529", "Ēku būvnoteikumi (MK Nr. 529)", "https://likumi.lv/ta/id/269164-eku-buvnoteikumi", BuildingRulesHash),
        new("general-building-rules-500", "Vispārīgie būvnoteikumi (MK Nr. 500)", "https://likumi.lv/ta/id/269069-visparigie-buvnoteikumi", GeneralBuildingRulesHash),
        new("law-protection-zones", "Aizsargjoslu likums", "https://likumi.lv/ta/id/42348-aizsargjoslu-likums", ProtectionZonesLawHash),
        new("law-cultural-heritage", "Likums \"Par kultūras pieminekļu aizsardzību\"", "https://likumi.lv/ta/id/72551-par-kulturas-piemineklu-aizsardzibu", HeritageHash),
        new("regulation-protected-nature", "Īpaši aizsargājamo dabas teritoriju vispārējie aizsardzības un izmantošanas noteikumi", "https://likumi.lv/ta/id/207283-ipasi-aizsargajamo-dabas-teritoriju-visparejie-aizsardzibas-un-izmantosanas-noteikumi", NatureHash),
        new("riga-territory-rules", "Rīgas teritorijas izmantošanas un apbūves noteikumi", "https://www.rdpad.lv/rtp/speka-esosais/", RigaRulesHash),
        new("riga-protection-zones", "RTP 2030 aizsargjoslu telpiskais slānis", "https://georiga.eu/", ProtectionZonesHash),
        new("riga-red-lines", "Rīgas ielu sarkanās līnijas", "https://data.gov.lv/dati/dataset/2376066a-293c-4063-b692-c63501a77d2d", RigaRedLinesHash)
    ];

    private static readonly RuleCitation HeritageCitation = new(
        "law-cultural-heritage", "3. pants", "https://likumi.lv/ta/id/72551-par-kulturas-piemineklu-aizsardzibu", HeritageHash);
    private static readonly RuleCitation NatureCitation = new(
        "regulation-protected-nature", "1. punkts", "https://likumi.lv/ta/id/118597", NatureHash);
    private static readonly RuleCitation ProtectionCitation = new(
        "riga-protection-zones", "RTP 2030 aizsargjoslu telpiskais slānis", "https://georiga.eu/", ProtectionZonesHash);
    private static readonly RuleCitation RigaCitation = new(
        "riga-territory-rules", "Pielikums Nr. 13 un Nr. 19", "https://www.rdpad.lv/rtp/speka-esosais/", RigaRulesHash);
    private static readonly RuleCitation ProtectionZonesLawCitation = new(
        "law-protection-zones", "1. pants", "https://likumi.lv/ta/id/42348-aizsargjoslu-likums", ProtectionZonesLawHash);
    private static readonly RuleCitation RedLinesCitation = new(
        "riga-red-lines", "Atvērtie ģeotelpiskie dati", "https://data.gov.lv/dati/dataset/2376066a-293c-4063-b692-c63501a77d2d", RigaRedLinesHash);

    public static readonly RegulatoryRule[] All =
    [
        Create("HERITAGE_REVIEW", "LV", ["*"], ["heritage"], false,
            RegulatoryStatuses.ManualReviewRequired, ReasonCodes.HeritageCoordinationLikely,
            ["Kultūras pieminekļa tuvumā pirms darbu sākšanas pārbaudiet NKMP vai pašvaldības kultūras mantojuma prasības."],
            [HeritageCitation]),
        Create("RIGA_HERITAGE_ZONE_REVIEW", "LV-RIX", ["*"],
            ["heritage_protection_zone", "urban_heritage_monument", "cultural_historic_territory", "building_protection_area"], true,
            RegulatoryStatuses.ManualReviewRequired, ReasonCodes.HeritageCoordinationLikely,
            ["Adrese atrodas Rīgas kultūrvēsturiskā vai apbūves aizsardzības teritorijā. Pirms darbu sākšanas pārbaudiet kompetentās iestādes prasības."],
            [HeritageCitation, ProtectionCitation, RigaCitation]),
        Create("PROTECTED_NATURE_CONDITIONS", "LV", ["solar-panels", "signboard", "terrace", "small-structure"],
            ["nature", "protected_nature_area", "microreserve", "coastal_protection_zone"], true,
            RegulatoryStatuses.ConditionsApply, ReasonCodes.NatureAuthorityCheckLikely,
            ["Aizsargājamā dabas teritorijā pirms ieceres īstenošanas pārbaudiet Dabas aizsardzības pārvaldes nosacījumus."],
            [NatureCitation]),
        Create("RIGA_RED_LINE_REVIEW", "LV-RIX", ["solar-panels", "terrace", "small-structure"], ["red_line"], true,
            RegulatoryStatuses.ManualReviewRequired, ReasonCodes.RedLineIntersects,
            ["Iecere skar Rīgas ielas sarkano līniju. Pirms turpmākas projektēšanas nepieciešama būvvaldes vai teritorijas plānojuma speciālista pārbaude."],
            [ProtectionZonesLawCitation, RigaCitation, RedLinesCitation]),
        Create("RIGA_FLOOD_RISK_CONDITIONS", "LV-RIX", ["solar-panels", "terrace", "small-structure"], ["flood_risk"], true,
            RegulatoryStatuses.ConditionsApply, ReasonCodes.FloodRiskIntersects,
            ["Adrese ir applūšanas riska teritorijā. Pirms ieceres īstenošanas pārbaudiet pašvaldības un nozaru iestāžu nosacījumus."],
            [ProtectionCitation, RigaCitation])
    ];

    private static RegulatoryRule Create(
        string ruleId,
        string jurisdiction,
        string[] intents,
        string[] flags,
        bool requiresIntersection,
        string status,
        string reasonCode,
        string[] nextSteps,
        RuleCitation[] citations) => new(
        ruleId,
        1,
        new DateOnly(2026, 10, 10),
        null,
        jurisdiction,
        intents,
        flags,
        requiresIntersection,
        status,
        reasonCode,
        nextSteps,
        citations);
}
