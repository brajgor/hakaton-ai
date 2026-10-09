namespace VaiDrikstu.Domain;

public static class ReasonCodes
{
    public const string AddressFound = "ADDRESS_FOUND";
    public const string AddressNotFound = "ADDRESS_NOT_FOUND";
    public const string HeritageSiteNearby = "HERITAGE_SITE_NEARBY";
    public const string HeritageSiteIntersects = "HERITAGE_SITE_INTERSECTS";
    public const string NatureAreaIntersects = "NATURE_AREA_INTERSECTS";
    public const string NoRestrictionFound = "NO_RESTRICTION_FOUND_IN_OPEN_DATA";
    public const string OfficialReviewRequired = "OFFICIAL_REVIEW_REQUIRED";
}

public static class ResultLevels
{
    public const string Green = "green";
    public const string Yellow = "yellow";
    public const string Red = "red";
}

public sealed record IntentDefinition(
    string Id,
    string Title,
    string Description,
    string[] TypicalNextSteps);

public sealed record AddressRecord(
    string Id,
    string NormalizedAddress,
    string ArCode,
    string Municipality,
    double Latitude,
    double Longitude);

public sealed record DataFinding(
    string Source,
    string Code,
    string Name,
    string Category,
    bool Intersects,
    double? DistanceMeters);

public sealed record PrecheckDecision(
    string Level,
    string[] ReasonCodes,
    DataFinding[] DataFindings,
    string[] NextSteps,
    string OfficialDisclaimer);
