namespace ArmaAiBridge.App.Models;

public enum GazetteerReadiness
{
    Unavailable,
    Receiving,
    Ready,
    Failed
}

public enum OperationalMemoryReadiness
{
    Unavailable,
    Ready,
    Failed
}

public enum OperationalProvenance
{
    Visual,
    Sensor,
    SideKnowledge,
    PlayerReport,
    MissionReport
}

public enum OperationalEntityKind
{
    Contact,
    Vehicle,
    Supply,
    Weapon,
    Fortification,
    Static,
    Other
}

public enum OperationalIdentityQuality
{
    StableMission,
    BestEffort,
    FusedReport
}

public sealed record MapGridSample(WorldPosition Position, string Grid);

public sealed record NamedLocationState(
    string Alias,
    string OfficialName,
    string LocationType,
    WorldPosition Position,
    double? SizeX,
    double? SizeY);

public sealed record OperationalObservationState(
    string Alias,
    string EntityAlias,
    string SourceAlias,
    OperationalProvenance Provenance,
    double ObservedAtGameTime,
    DateTimeOffset ReceivedAtUtc,
    WorldPosition? Position,
    double? PositionErrorMeters,
    double BaseConfidence,
    string Classification,
    string PerceivedSide,
    string State,
    string Summary,
    IReadOnlyList<string> Corroborates,
    IReadOnlyList<string> Contradicts,
    bool ConstraintConflict,
    string ConstraintLocationAlias,
    WorldPosition? ConstraintPosition,
    double? ConstraintRadiusMeters,
    string Supersedes,
    DateTimeOffset? RetractedAtUtc);

public sealed record OperationalEntityState(
    string Alias,
    OperationalEntityKind Kind,
    OperationalIdentityQuality IdentityQuality,
    string Classification,
    string DisplayLabel,
    string PerceivedSide,
    double FirstObservedAtGameTime,
    double LastObservedAtGameTime,
    DateTimeOffset LastReceivedAtUtc,
    WorldPosition? Position,
    double? PositionErrorMeters,
    WorldFreshness Freshness,
    double Confidence,
    string State,
    int ConflictCount,
    int CorroborationCount,
    bool IsLastKnown,
    bool IsRetracted);

public sealed record OperationalMemoryView(
    long Version,
    OperationalMemoryReadiness Readiness,
    GazetteerReadiness GazetteerReadiness,
    string SessionAlias,
    string WorldName,
    int SchemaVersion,
    string DatabasePath,
    string GazetteerFingerprintAlias,
    int GazetteerLocationCount,
    IReadOnlyList<NamedLocationState> NamedLocations,
    IReadOnlyList<OperationalEntityState> Entities,
    IReadOnlyList<OperationalObservationState> Observations,
    string LastBatchAlias,
    string DiagnosticCode);
