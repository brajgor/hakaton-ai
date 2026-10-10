using VaiDrikstu.Domain;
using VaiDrikstu.Domain.Assistant;
using Xunit;

namespace VaiDrikstu.Domain.Tests;

/// <summary>Loads the real generated knowledge base once for all assistant tests.</summary>
public sealed class KnowledgeBaseFixture
{
    public KnowledgeBaseFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VaiDrikstu.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        KnowledgeBase = KnowledgeBase.Load(Path.Combine(directory!.FullName, "data", "assistant"));
        Engine = new AssistantEngine(KnowledgeBase);
    }

    public KnowledgeBase KnowledgeBase { get; }

    public AssistantEngine Engine { get; }
}

public sealed class AssistantEngineTests(KnowledgeBaseFixture fixture) : IClassFixture<KnowledgeBaseFixture>
{
    private static readonly AssistantContext YellowHeritage = new(
        ResultLevels.Yellow,
        [ReasonCodes.AddressFound, ReasonCodes.HeritageSiteNearby, ReasonCodes.HeritageCoordinationLikely, ReasonCodes.OfficialReviewRequired],
        [new DataFinding("heritage", "NKMP-DEMO-1", "Rīgas pils", "Valsts nozīmes arhitektūras piemineklis", false, 42.3)],
        ["Saskaņojiet ieceri ar NKMP.", "Pārbaudiet būvvaldes prasības BIS."]);

    private AssistantEngine Engine => fixture.Engine;

    [Fact]
    public void KnowledgeBaseContainsAllDictionaries()
    {
        var kinds = fixture.KnowledgeBase.Labels.Select(label => label.Kind).ToHashSet();
        foreach (var kind in new[] { "reason", "level", "encumbrance", "mark", "zone", "zone-rvc", "tin", "protection-zone", "nature-area", "address-column" })
        {
            Assert.Contains(kind, kinds);
        }

        Assert.True(fixture.KnowledgeBase.Documents.Count(document => document.Kind == "regulation") > 500);
        Assert.True(fixture.KnowledgeBase.Monuments.Count > 8000);
        Assert.True(fixture.KnowledgeBase.CadastreMarks.Count > 40000);
    }

    [Fact]
    public void EveryRuleEngineReasonCodeHasAnExplanation()
    {
        var codes = typeof(ReasonCodes).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);

        foreach (var code in codes)
        {
            var label = fixture.KnowledgeBase.Find("reason", code);
            Assert.True(label is not null, $"Missing assistant explanation for {code}");
            Assert.NotNull(label!.Translations?["ru"]);
            Assert.NotNull(label.Translations?["en"]);
        }
    }

    [Theory]
    [InlineData("Ko nozīmē HERITAGE_SITE_NEARBY?", "lv", "Kultūras piemineklis tuvumā")]
    [InlineData("Что значит HERITAGE_SITE_NEARBY?", "ru", "Рядом памятник культуры")]
    [InlineData("What does DAP_CONDITIONS_LIKELY mean?", "en", "DAP conditions likely")]
    public void ExplainsReasonCodesInTheQuestionLanguage(string question, string language, string expectedTitle)
    {
        var answer = Engine.Answer(question, YellowHeritage);

        Assert.Equal(language, answer.Language);
        Assert.True(answer.Found);
        Assert.Equal(expectedTitle, answer.Items[0].Title);
        Assert.Equal("reason", answer.Items[0].Kind);
    }

    [Fact]
    public void DecodesVzdEncumbranceCode()
    {
        var answer = Engine.Answer("Ko nozīmē apgrūtinājums 7312030100?");

        var item = Assert.Single(answer.Items);
        Assert.Equal("encumbrance", item.Kind);
        Assert.Equal("7312030100", item.Code);
        Assert.Contains("sarkanā līnija", item.Title);
        Assert.Contains(item.Details, detail => detail.StartsWith("VZD datos", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodesCadastreMarksByCadastreDesignation()
    {
        var answer = Engine.Answer("Какие отметки у 01000770210099?");

        Assert.Equal("ru", answer.Language);
        var item = Assert.Single(answer.Items, candidate => candidate.Kind == "cadastre");
        Assert.Contains(item.Details, detail => detail.Contains("Atsavināšanas aizliegumi"));
    }

    [Fact]
    public void DecodesMarkTypeWhenAskedAboutAMark()
    {
        var answer = Engine.Answer("Ko nozīmē atzīme 0802?");

        var item = Assert.Single(answer.Items);
        Assert.Equal("mark", item.Kind);
        Assert.Equal("Koordinātu nesakritība", item.Title);
    }

    [Theory]
    [InlineData("Kas ir DzS1?", "zone", "Savrupmāju")]
    [InlineData("что такое зона dzs1", "zone", "особняками")]
    [InlineData("TIN14", "tin", "apzaļumojuma")]
    [InlineData("AZ-37", "protection-zone", "dzelzceļu")]
    [InlineData("Kas ir DLZ?", "nature-zone", "Dabas lieguma zona")]
    public void DecodesZoneAndAreaCodes(string question, string kind, string expected)
    {
        var answer = Engine.Answer(question);

        var item = Assert.Single(answer.Items, candidate => candidate.Kind == kind);
        Assert.Contains(expected, item.Title + " " + item.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameLettersInRigaPlanAndHistoricCentreAreBothShown()
    {
        var answer = Engine.Answer("Kas ir JC1?");

        Assert.Contains(answer.Items, item => item.Kind == "zone");
        Assert.Contains(answer.Items, item => item.Kind == "zone-rvc");
    }

    [Theory]
    [InlineData("почему жёлтый?", "ru")]
    [InlineData("Kāpēc šāds rezultāts?", "lv")]
    [InlineData("Why this result?", "en")]
    public void ExplainsThePrecheckResult(string question, string language)
    {
        var answer = Engine.Answer(question, YellowHeritage);

        Assert.Equal(language, answer.Language);
        Assert.Equal("level", answer.Items[0].Kind);
        Assert.Contains(answer.Items, item => item.Code == ReasonCodes.HeritageSiteNearby);
        Assert.Contains(answer.Items, item => item.Kind == "finding" && item.Title == "Rīgas pils");
        Assert.DoesNotContain(answer.Items, item => item.Code == ReasonCodes.AddressFound);
    }

    [Fact]
    public void ReturnsNextStepsFromTheResult()
    {
        var answer = Engine.Answer("Kas man jādara tālāk?", YellowHeritage);

        var item = Assert.Single(answer.Items);
        Assert.Equal("steps", item.Kind);
        Assert.Contains("Saskaņojiet ieceri ar NKMP.", item.Details);
    }

    [Theory]
    [InlineData("охранная зона вдоль железной дороги", "dzelzce")]
    [InlineData("aizsargjosla gar dzelzceļu", "dzelzce")]
    [InlineData("protection zone along railway", "dzelzce")]
    public void FindsProtectionZonesByFreeText(string question, string expectedFragment)
    {
        var answer = Engine.Answer(question);

        Assert.True(answer.Found);
        Assert.Contains(answer.Items, item => TextAnalysis.Fold(item.Title + " " + item.Text).Contains(expectedFragment));
    }

    [Fact]
    public void FindsRegulationArticlesByFreeText()
    {
        var answer = Engine.Answer("kultūras pieminekļa aizsardzības zona");

        Assert.Contains(answer.Items, item => item.Kind is "regulation" or "protection-zone" or "encumbrance");
    }

    [Fact]
    public void FindsMonumentByName()
    {
        var answer = Engine.Answer("piemineklis Endzelīna dzīves vieta");

        Assert.Contains(answer.Items, item => item.Kind == "monument" && item.Title.Contains("Endzelīna"));
    }

    [Fact]
    public void UnknownQuestionReturnsNotFoundWithSuggestions()
    {
        var answer = Engine.Answer("qwertyuiop zxcvbnm");

        Assert.False(answer.Found);
        Assert.Empty(answer.Items);
        Assert.NotEmpty(answer.Suggestions);
    }

    [Fact]
    public void EmptyQuestionReturnsHelpWithResultSuggestions()
    {
        var answer = Engine.Answer(string.Empty, YellowHeritage, "ru");

        Assert.Equal("ru", answer.Language);
        Assert.Contains("помощник", answer.Intro);
        Assert.Contains(answer.Suggestions, suggestion => suggestion.Contains(ReasonCodes.HeritageSiteNearby));
    }

    [Fact]
    public void AnswersAreDeterministic()
    {
        var first = Engine.Answer("aizsargjosla ap kapsētu");
        var second = Engine.Answer("aizsargjosla ap kapsētu");

        Assert.Equal(first.Items.Select(item => item.Code), second.Items.Select(item => item.Code));
    }

    [Theory]
    [InlineData("Ko nozīmē kods?", "lv")]
    [InlineData("Что значит этот код?", "ru")]
    [InlineData("What does it mean?", "en")]
    [InlineData("Kas ir sarkanā līnija", "lv")]
    public void DetectsLanguage(string question, string expected) =>
        Assert.Equal(expected, TextAnalysis.DetectLanguage(question));
}
