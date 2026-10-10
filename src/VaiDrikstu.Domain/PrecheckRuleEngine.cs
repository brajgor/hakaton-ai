namespace VaiDrikstu.Domain;

public static class PrecheckRuleEngine
{
    public const string Disclaimer =
        "Šī ir priekšpārbaude, nevis galīgā atļauja. Galīgo lēmumu pieņem kompetentā iestāde.";

    public const string NotCheckedNote =
        "Netika pārbaudīts: lokālplānojumu un detālplānojumu individuālie nosacījumi, precīza ieceres ģeometrija, zemesgabala apgrūtinājumi, kopīpašnieku vai dzīvokļu īpašnieku piekrišana.";

    // Backwards-compatible alias used by the MVP tests and any existing callers.
    public static readonly RegulatoryRule[] Rules = RuleCatalog.All;

    public static PrecheckDecision Evaluate(
        IntentDefinition? intent,
        AddressRecord? address,
        IReadOnlyCollection<DataFinding> findings,
        DateOnly? checkedOn = null,
        IReadOnlyCollection<RegulatoryRule>? rules = null)
    {
        if (address is null)
        {
            return new PrecheckDecision(
                ResultLevels.Red,
                RegulatoryStatuses.InsufficientData,
                [ReasonCodes.AddressNotFound, ReasonCodes.OfficialReviewRequired],
                [],
                ["Precizējiet adresi ar Valsts adrešu reģistra datiem.", "Ja adrese nav atrodama, sazinieties ar pašvaldību vai VZD.", NotCheckedNote],
                ["Adrese netika atrasta, tādēļ telpisko un normatīvo pārbaudi nevar pabeigt."],
                [],
                Disclaimer);
        }

        var date = checkedOn ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var applicableRules = (rules ?? RuleCatalog.All)
            .Where(rule => rule.EffectiveFrom <= date && (rule.EffectiveTo is null || rule.EffectiveTo >= date))
            .Where(rule => MatchesJurisdiction(rule, address.Municipality))
            .ToArray();

        var reasons = new List<string> { ReasonCodes.AddressFound };
        var nextSteps = new List<string>();
        var applied = new List<AppliedRule>();

        foreach (var finding in findings)
        {
            AddFindingReason(reasons, finding);

            foreach (var rule in applicableRules.Where(rule => Matches(rule, intent, finding)))
            {
                if (applied.Any(current => current.RuleId == rule.RuleId && current.Version == rule.Version))
                {
                    continue;
                }

                applied.Add(new AppliedRule(rule.RuleId, rule.Version, rule.Status, rule.ReasonCode, rule.Citations));
                reasons.Add(rule.ReasonCode);
                nextSteps.AddRange(rule.NextSteps);
            }
        }

        var status = applied.Count == 0
            ? RegulatoryStatuses.NoKnownBlocker
            : applied.Select(rule => rule.Status).OrderByDescending(StatusRank).First();

        if (status == RegulatoryStatuses.NoKnownBlocker)
        {
            reasons.Add(ReasonCodes.NoRestrictionFound);
            nextSteps.Add("Atvērtajos datos šai adresei netika atrasts izvēlētajai darbībai būtisks ierobežojums.");
            nextSteps.Add("Turpiniet ar atbilstošo BIS vai pašvaldības e-pakalpojumu, ja izvēlētajai darbībai tas ir nepieciešams.");
        }
        else
        {
            reasons.Add(ReasonCodes.OfficialReviewRequired);
            nextSteps.Add("Saglabājiet šo priekšpārbaudes kopsavilkumu kā pamatu tālākai e-pakalpojuma izpildei.");
        }

        if (intent is not null)
        {
            nextSteps.AddRange(intent.TypicalNextSteps);
        }

        nextSteps.Add(NotCheckedNote);
        string[] coverageWarnings = address.Municipality == "Rīga"
            ? ["Rīgas telpiskie slāņi ir pārbaudīti; individuāli lokālplānojumu un detālplānojumu nosacījumi var nebūt pilnībā ietverti."]
            : ["Ārpus Rīgas nav ielādēti visi pašvaldību teritorijas plānojuma un aizsargjoslu slāņi."];

        return new PrecheckDecision(
            ToLevel(status),
            status,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            findings.ToArray(),
            nextSteps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            coverageWarnings,
            applied.ToArray(),
            Disclaimer);
    }

    private static bool Matches(RegulatoryRule rule, IntentDefinition? intent, DataFinding finding)
    {
        var hasIntent = intent is not null &&
                        (rule.IntentAny.Contains("*") || rule.IntentAny.Contains(intent.Id, StringComparer.OrdinalIgnoreCase));
        var hasSpatialFlag = rule.SpatialFlagsAny.Contains(finding.Source, StringComparer.OrdinalIgnoreCase);
        return hasIntent && hasSpatialFlag && (!rule.RequiresIntersection || finding.Intersects);
    }

    private static bool MatchesJurisdiction(RegulatoryRule rule, string municipality) =>
        rule.Jurisdiction == "LV" || (rule.Jurisdiction == "LV-RIX" && municipality == "Rīga");

    private static void AddFindingReason(ICollection<string> reasons, DataFinding finding)
    {
        var code = finding.Source switch
        {
            "heritage" => finding.Intersects ? ReasonCodes.HeritageSiteIntersects : ReasonCodes.HeritageSiteNearby,
            "nature" or "protected_nature_area" => ReasonCodes.NatureAreaIntersects,
            "red_line" => ReasonCodes.RedLineIntersects,
            "heritage_protection_zone" or "urban_heritage_monument" or "cultural_historic_territory" or "building_protection_area" => ReasonCodes.HeritageProtectionZoneIntersects,
            "flood_risk" => ReasonCodes.FloodRiskIntersects,
            "planning_zone" => ReasonCodes.PlanningZoneFound,
            _ => null
        };

        if (code is not null)
        {
            reasons.Add(code);
        }
    }

    private static int StatusRank(string status) => status switch
    {
        RegulatoryStatuses.InsufficientData => 3,
        RegulatoryStatuses.ManualReviewRequired => 2,
        RegulatoryStatuses.ConditionsApply => 1,
        _ => 0
    };

    private static string ToLevel(string status) => status switch
    {
        RegulatoryStatuses.NoKnownBlocker => ResultLevels.Green,
        RegulatoryStatuses.InsufficientData => ResultLevels.Red,
        _ => ResultLevels.Yellow
    };
}
