using VaiDrikstu.Domain;
using Xunit;

namespace VaiDrikstu.Domain.Tests;

public sealed class PrecheckRuleEngineTests
{
    private static readonly IntentDefinition Intent = IntentCatalog.All[0];
    private static readonly AddressRecord Address = new(
        "addr-1",
        "Demo iela 1, Rīga",
        "AR-1",
        "Rīga",
        56.95,
        24.10);

    [Fact]
    public void AddressNotFoundReturnsRed()
    {
        var decision = PrecheckRuleEngine.Evaluate(Intent, null, []);

        Assert.Equal(ResultLevels.Red, decision.Level);
        Assert.Equal(RegulatoryStatuses.InsufficientData, decision.RegulatoryStatus);
        Assert.Contains(ReasonCodes.AddressNotFound, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.OfficialReviewRequired, decision.ReasonCodes);
        Assert.Contains("priekšpārbaude", decision.OfficialDisclaimer);
    }

    [Fact]
    public void HeritageIntersectionReturnsYellow()
    {
        var decision = PrecheckRuleEngine.Evaluate(Intent, Address, [
            new DataFinding("heritage", "NKMP-1", "Piemineklis", "Kultūras piemineklis", true, 0)
        ]);

        Assert.Equal(ResultLevels.Yellow, decision.Level);
        Assert.Equal(RegulatoryStatuses.ManualReviewRequired, decision.RegulatoryStatus);
        Assert.Contains(ReasonCodes.AddressFound, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.HeritageSiteIntersects, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.OfficialReviewRequired, decision.ReasonCodes);
    }

    [Fact]
    public void NatureIntersectionReturnsYellow()
    {
        var decision = PrecheckRuleEngine.Evaluate(Intent, Address, [
            new DataFinding("nature", "DAP-1", "Dabas teritorija", "ĪADT", true, null)
        ]);

        Assert.Equal(ResultLevels.Yellow, decision.Level);
        Assert.Equal(RegulatoryStatuses.ConditionsApply, decision.RegulatoryStatus);
        Assert.Contains(ReasonCodes.NatureAreaIntersects, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.OfficialReviewRequired, decision.ReasonCodes);
    }

    [Fact]
    public void NoFindingsReturnsGreen()
    {
        var decision = PrecheckRuleEngine.Evaluate(Intent, Address, []);

        Assert.Equal(ResultLevels.Green, decision.Level);
        Assert.Equal(RegulatoryStatuses.NoKnownBlocker, decision.RegulatoryStatus);
        Assert.Contains(ReasonCodes.AddressFound, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.NoRestrictionFound, decision.ReasonCodes);
        Assert.DoesNotContain(ReasonCodes.OfficialReviewRequired, decision.ReasonCodes);
    }

    [Fact]
    public void SolarPanelsNearHeritageRequireNkmpCoordination()
    {
        var decision = PrecheckRuleEngine.Evaluate(IntentCatalog.Find("solar-panels"), Address, [
            new DataFinding("heritage", "NKMP-1", "Piemineklis", "Kultūras piemineklis", false, 80)
        ]);

        Assert.Equal(ResultLevels.Yellow, decision.Level);
        Assert.Contains(ReasonCodes.HeritageSiteNearby, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.HeritageCoordinationLikely, decision.ReasonCodes);
        Assert.Contains(decision.NextSteps, step => step.Contains("NKMP"));
        var rule = Assert.Single(decision.AppliedRules);
        Assert.Equal("HERITAGE_REVIEW", rule.RuleId);
        Assert.Equal(1, rule.Version);
        Assert.Contains(rule.Citations, citation => citation.SourceId == "law-cultural-heritage");
    }

    [Fact]
    public void WindowsInNatureAreaAreNotFlagged()
    {
        var decision = PrecheckRuleEngine.Evaluate(IntentCatalog.Find("facade-windows"), Address, [
            new DataFinding("nature", "DAP-1", "Dabas teritorija", "ĪADT", true, null)
        ]);

        Assert.Equal(ResultLevels.Green, decision.Level);
        Assert.Contains(ReasonCodes.NatureAreaIntersects, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.NoRestrictionFound, decision.ReasonCodes);
    }

    [Fact]
    public void SameAddressGivesDifferentResultForDifferentIntent()
    {
        DataFinding[] natureFinding = [new DataFinding("nature", "DAP-1", "Dabas teritorija", "ĪADT", true, null)];

        var smallStructure = PrecheckRuleEngine.Evaluate(IntentCatalog.Find("small-structure"), Address, natureFinding);
        var windows = PrecheckRuleEngine.Evaluate(IntentCatalog.Find("facade-windows"), Address, natureFinding);

        Assert.Equal(ResultLevels.Yellow, smallStructure.Level);
        Assert.Contains(ReasonCodes.NatureAuthorityCheckLikely, smallStructure.ReasonCodes);
        Assert.Equal(ResultLevels.Green, windows.Level);
    }

    [Fact]
    public void ResultAlwaysListsWhatWasNotChecked()
    {
        var decision = PrecheckRuleEngine.Evaluate(Intent, Address, []);

        Assert.Contains(PrecheckRuleEngine.NotCheckedNote, decision.NextSteps);
    }

    [Fact]
    public void EveryIntentHasAHeritageRule()
    {
        Assert.Contains(PrecheckRuleEngine.Rules, rule =>
            rule.IntentAny.Contains("*") && rule.SpatialFlagsAny.Contains("heritage"));
    }

    [Fact]
    public void RigaRedLineForTerraceRequiresManualReview()
    {
        var decision = PrecheckRuleEngine.Evaluate(IntentCatalog.Find("terrace"), Address, [
            new DataFinding("red_line", "RL-1", "Sarkanā līnija", "Rīga", true, null)
        ]);

        Assert.Equal(ResultLevels.Yellow, decision.Level);
        Assert.Equal(RegulatoryStatuses.ManualReviewRequired, decision.RegulatoryStatus);
        Assert.Contains(ReasonCodes.RedLineIntersects, decision.ReasonCodes);
        Assert.Contains(decision.AppliedRules, rule => rule.RuleId == "RIGA_RED_LINE_REVIEW");
    }
}
