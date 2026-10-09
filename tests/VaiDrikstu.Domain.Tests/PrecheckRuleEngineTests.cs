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
        Assert.Contains(ReasonCodes.NatureAreaIntersects, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.OfficialReviewRequired, decision.ReasonCodes);
    }

    [Fact]
    public void NoFindingsReturnsGreen()
    {
        var decision = PrecheckRuleEngine.Evaluate(Intent, Address, []);

        Assert.Equal(ResultLevels.Green, decision.Level);
        Assert.Contains(ReasonCodes.AddressFound, decision.ReasonCodes);
        Assert.Contains(ReasonCodes.NoRestrictionFound, decision.ReasonCodes);
        Assert.DoesNotContain(ReasonCodes.OfficialReviewRequired, decision.ReasonCodes);
    }
}
