namespace VaiDrikstu.Domain;

public static class ReasonCodes
{
    public const string AddressFound = "ADDRESS_FOUND";
    public const string AddressNotFound = "ADDRESS_NOT_FOUND";
    public const string HeritageSiteNearby = "HERITAGE_SITE_NEARBY";
    public const string HeritageSiteIntersects = "HERITAGE_SITE_INTERSECTS";
    public const string NatureAreaIntersects = "NATURE_AREA_INTERSECTS";
    public const string RedLineIntersects = "RED_LINE_INTERSECTS";
    public const string HeritageProtectionZoneIntersects = "HERITAGE_PROTECTION_ZONE_INTERSECTS";
    public const string FloodRiskIntersects = "FLOOD_RISK_INTERSECTS";
    public const string PlanningZoneFound = "PLANNING_ZONE_FOUND";
    public const string RuleDataCoverageLimited = "RULE_DATA_COVERAGE_LIMITED";
    public const string NoRestrictionFound = "NO_RESTRICTION_FOUND_IN_OPEN_DATA";
    public const string OfficialReviewRequired = "OFFICIAL_REVIEW_REQUIRED";

    // Likely procedures, depending on the selected intent
    public const string HeritageCoordinationLikely = "NKMP_COORDINATION_LIKELY";
    public const string NatureAuthorityCheckLikely = "DAP_CONDITIONS_LIKELY";
    public const string MunicipalApprovalRequired = "MUNICIPAL_APPROVAL_LIKELY";
}

public static class ResultLevels
{
    public const string Green = "green";
    public const string Yellow = "yellow";
    public const string Red = "red";
}

public static class RegulatoryStatuses
{
    public const string NoKnownBlocker = "no_known_blocker";
    public const string ConditionsApply = "conditions_apply";
    public const string ManualReviewRequired = "manual_review_required";
    public const string InsufficientData = "insufficient_data";
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

public sealed record ProjectParameters(
    string? Placement,
    decimal? AreaSquareMeters,
    decimal? HeightMeters,
    int? FloorCount,
    bool? ChangesFacade);

public sealed record RuleCitation(
    string SourceId,
    string Section,
    string OfficialUrl,
    string SourceSnapshotSha256);

public sealed record LegalSourceSnapshot(
    string SourceId,
    string Title,
    string OfficialUrl,
    string SnapshotSha256);

public sealed record RegulatoryRule(
    string RuleId,
    int Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string Jurisdiction,
    string[] IntentAny,
    string[] SpatialFlagsAny,
    bool RequiresIntersection,
    string Status,
    string ReasonCode,
    string[] NextSteps,
    RuleCitation[] Citations);

public sealed record AppliedRule(
    string RuleId,
    int Version,
    string Status,
    string ReasonCode,
    RuleCitation[] Citations);

public sealed record PrecheckDecision(
    string Level,
    string RegulatoryStatus,
    string[] ReasonCodes,
    DataFinding[] DataFindings,
    string[] NextSteps,
    string[] CoverageWarnings,
    AppliedRule[] AppliedRules,
    string OfficialDisclaimer);
