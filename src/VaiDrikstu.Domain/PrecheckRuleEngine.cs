namespace VaiDrikstu.Domain;

public static class PrecheckRuleEngine
{
    public const string Disclaimer =
        "Šī ir priekšpārbaude, nevis galīgā atļauja. Galīgo lēmumu pieņem kompetentā iestāde.";

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
            hasRestriction = true;

            if (finding.Source == "heritage" && finding.Intersects)
            {
                reasons.Add(ReasonCodes.HeritageSiteIntersects);
            }
            else if (finding.Source == "heritage")
            {
                reasons.Add(ReasonCodes.HeritageSiteNearby);
            }
            else if (finding.Source == "nature")
            {
                reasons.Add(ReasonCodes.NatureAreaIntersects);
            }
        }

        if (hasRestriction)
        {
            reasons.Add(ReasonCodes.OfficialReviewRequired);
            nextSteps.Add("Pirms darbu sākšanas pārbaudiet oficiālās saskaņošanas prasības kompetentajā iestādē.");
            nextSteps.Add("Saglabājiet šo priekšpārbaudes kopsavilkumu kā pamatu tālākai e-pakalpojuma izpildei.");
        }
        else
        {
            reasons.Add(ReasonCodes.NoRestrictionFound);
            nextSteps.Add("Atvērtajos datos šai adresei netika atrasts kultūras mantojuma vai dabas teritorijas ierobežojums.");
            nextSteps.Add("Turpiniet ar atbilstošo BIS vai pašvaldības e-pakalpojumu, ja izvēlētajai darbībai tas ir nepieciešams.");
        }

        if (intent is not null)
        {
            nextSteps.AddRange(intent.TypicalNextSteps);
        }

        return new PrecheckDecision(
            hasRestriction ? ResultLevels.Yellow : ResultLevels.Green,
            reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            findings.ToArray(),
            nextSteps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Disclaimer);
    }
}
