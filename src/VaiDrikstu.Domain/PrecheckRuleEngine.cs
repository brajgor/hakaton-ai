namespace VaiDrikstu.Domain;

/// <summary>
/// One row of the rule table: what a given restriction means for a given intent.
/// The table is deliberately data-like so non-developers can review it.
/// </summary>
public sealed record IntentRestrictionRule(
    string IntentId,
    string Source,
    bool RequiresIntersection,
    string ProcedureCode,
    string NextStep);

public static class PrecheckRuleEngine
{
    public const string Disclaimer =
        "Šī ir priekšpārbaude, nevis galīgā atļauja. Galīgo lēmumu pieņem kompetentā iestāde.";

    public const string NotCheckedNote =
        "Netika pārbaudīts: teritorijas plānojums un pašvaldības saistošie noteikumi, zemesgabala apgrūtinājumi, " +
        "kopīpašnieku vai dzīvokļu īpašnieku piekrišana.";

    private const string Heritage = "heritage";
    private const string Nature = "nature";

    /// <summary>
    /// Intent × restriction → likely official procedure.
    /// RequiresIntersection = true means the rule applies only when the address lies inside the area;
    /// false means a nearby object (within the search radius) is enough.
    /// </summary>
    public static readonly IntentRestrictionRule[] Rules =
    [
        // Solar panels
        new("solar-panels", Heritage, false, ReasonCodes.HeritageCoordinationLikely,
            "Saules paneļi var mainīt ēkas ārējo veidolu: pirms uzstādīšanas saskaņojiet ieceri ar Nacionālo kultūras mantojuma pārvaldi (NKMP)."),
        new("solar-panels", Nature, true, ReasonCodes.NatureAuthorityCheckLikely,
            "Uz zemes izvietotiem saules paneļiem aizsargājamā dabas teritorijā pārbaudiet Dabas aizsardzības pārvaldes (DAP) nosacījumus."),

        // Signboard
        new("signboard", Heritage, false, ReasonCodes.HeritageCoordinationLikely,
            "Izkārtnei kultūras mantojuma objektā vai tā tuvumā parasti vajadzīga NKMP saskaņošana kopā ar pašvaldības saskaņojumu."),
        new("signboard", Nature, true, ReasonCodes.MunicipalApprovalRequired,
            "Izkārtnes izvietojumu dabas teritorijā saskaņojiet ar pašvaldību; DAP nosacījumi var attiekties uz brīvstāvošām konstrukcijām."),

        // Windows / facade
        new("facade-windows", Heritage, false, ReasonCodes.HeritageCoordinationLikely,
            "Logu vai fasādes maiņa kultūras mantojuma objektā vai tā aizsardzības zonā jāsaskaņo ar NKMP pirms darbu sākšanas."),
        // No nature rule on purpose: changing windows of an existing building is usually
        // not affected by the protected nature area status (checked by tests).

        // Terrace
        new("terrace", Heritage, false, ReasonCodes.HeritageCoordinationLikely,
            "Āra terasei vēsturiskā vidē pašvaldība parasti prasa NKMP saskaņotu novietojuma un vizuālā risinājuma skici."),
        new("terrace", Nature, true, ReasonCodes.NatureAuthorityCheckLikely,
            "Terasei aizsargājamā dabas teritorijā pārbaudiet DAP nosacījumus, īpaši kāpu un piekrastes zonā."),

        // Small auxiliary building
        new("small-structure", Heritage, false, ReasonCodes.HeritageCoordinationLikely,
            "Palīgēkai kultūras mantojuma teritorijā vai tās tuvumā būvniecības iecere BIS būs jāsaskaņo arī ar NKMP."),
        new("small-structure", Nature, true, ReasonCodes.NatureAuthorityCheckLikely,
            "Jaunbūvei aizsargājamā dabas teritorijā var būt nepieciešami DAP tehniskie noteikumi vai ietekmes izvērtējums; sāciet ar būvniecības ieceri BIS."),
    ];

    public static PrecheckDecision Evaluate(
        IntentDefinition? intent,
        AddressRecord? address,
        IReadOnlyCollection<DataFinding> findings)
    {
        if (address is null)
        {
            return new PrecheckDecision(
                ResultLevels.Red,
                [ReasonCodes.AddressNotFound, ReasonCodes.OfficialReviewRequired],
                [],
                ["Precizējiet adresi ar Valsts adrešu reģistra datiem.", "Ja adrese nav atrodama, sazinieties ar pašvaldību vai VZD."],
                Disclaimer);
        }

        var reasons = new List<string> { ReasonCodes.AddressFound };
        var nextSteps = new List<string>();
        var hasRestriction = false;

        foreach (var finding in findings)
        {
            // Which raw data signal did we find?
            if (finding.Source == Heritage)
            {
                reasons.Add(finding.Intersects ? ReasonCodes.HeritageSiteIntersects : ReasonCodes.HeritageSiteNearby);
            }
            else if (finding.Source == Nature && finding.Intersects)
            {
                reasons.Add(ReasonCodes.NatureAreaIntersects);
            }

            // What does that signal mean for the selected intent?
            var rule = FindRule(intent, finding);
            if (rule is not null)
            {
                hasRestriction = true;
                reasons.Add(rule.ProcedureCode);
                nextSteps.Add(rule.NextStep);
            }
            else if (intent is null && (finding.Source == Heritage || finding.Intersects))
            {
                // Unknown intent: stay conservative and flag any restriction.
                hasRestriction = true;
            }
        }

        if (hasRestriction)
        {
            reasons.Add(ReasonCodes.OfficialReviewRequired);
            nextSteps.Add("Saglabājiet šo priekšpārbaudes kopsavilkumu kā pamatu tālākai e-pakalpojuma izpildei.");
        }
        else
        {
            reasons.Add(ReasonCodes.NoRestrictionFound);
            nextSteps.Add("Atvērtajos datos šai adresei netika atrasts izvēlētajai darbībai būtisks kultūras mantojuma vai dabas teritorijas ierobežojums.");
            nextSteps.Add("Turpiniet ar atbilstošo BIS vai pašvaldības e-pakalpojumu, ja izvēlētajai darbībai tas ir nepieciešams.");
        }

        if (intent is not null)
        {
            nextSteps.AddRange(intent.TypicalNextSteps);
        }

        nextSteps.Add(NotCheckedNote);

        return new PrecheckDecision(
            hasRestriction ? ResultLevels.Yellow : ResultLevels.Green,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            findings.ToArray(),
            nextSteps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Disclaimer);
    }

    private static IntentRestrictionRule? FindRule(IntentDefinition? intent, DataFinding finding)
    {
        if (intent is null)
        {
            return null;
        }

        return Rules.FirstOrDefault(rule =>
            string.Equals(rule.IntentId, intent.Id, StringComparison.OrdinalIgnoreCase)
            && rule.Source == finding.Source
            && (!rule.RequiresIntersection || finding.Intersects));
    }
}
