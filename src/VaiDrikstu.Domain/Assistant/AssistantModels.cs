using System.Text.Json;

namespace VaiDrikstu.Domain.Assistant;

/// <summary>One entry of a code dictionary (reason code, VZD encumbrance code, zone code ...).</summary>
public sealed record KnowledgeLabel(
    string Kind,
    string Code,
    string Title,
    string Text,
    string Source,
    string? Url = null,
    string[]? Aliases = null,
    Dictionary<string, JsonElement>? Meta = null,
    Dictionary<string, LabelTranslation>? Translations = null);

public sealed record LabelTranslation(string Title, string Text);

/// <summary>A searchable text: regulation article or dataset description.</summary>
public sealed record KnowledgeDocument(
    string Id,
    string Kind,
    string Title,
    string Ref,
    string Text,
    string Source,
    string? Url = null,
    string[]? Codes = null);

/// <summary>Row of the NKMP list of state protected cultural monuments.</summary>
public sealed record MonumentRecord(
    string Nr,
    string Name,
    string Group,
    string Value,
    string Dating,
    string Location,
    string Since,
    string Condition,
    string Access,
    bool Movable,
    string? EncumbranceCode,
    string[] Cadastre);

/// <summary>VZD cadastre marks of one cadastre object. <c>M</c> holds [markType, markDate] pairs.</summary>
public sealed record CadastreMarks(string T, string?[][] M);

/// <summary>Result of a pre-check the user is asking about.</summary>
public sealed record AssistantContext(
    string Level,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<DataFinding> Findings,
    IReadOnlyList<string> NextSteps,
    string? IntentId = null,
    string? AddressLabel = null);

public sealed record AssistantItem(
    string Kind,
    string KindLabel,
    string Code,
    string Title,
    string Text,
    string Source,
    string? Url,
    string[] Details,
    bool OriginalLanguage);

public sealed record AssistantAnswer(
    string Language,
    string Intro,
    AssistantItem[] Items,
    string[] Suggestions,
    string Disclaimer,
    bool Found);
