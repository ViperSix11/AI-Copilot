namespace ArmaAiBridge.App.Models;

public enum WorldFreshness
{
    Live,
    Recent,
    Stale,
    Historical
}

public enum WorldProvenance
{
    Player,
    Group,
    Sensor,
    Mission,
    Derived
}

public enum EntityIdentityQuality
{
    Stable,
    BestEffort,
    Slot
}

public enum WorldResetReason
{
    InitialTelemetry,
    MapChanged,
    MissionTimeRegressed,
    FrameRegressed
}

public sealed record WorldPosition(double X, double Y, double Z);

public sealed record WorldEntityMetadata(
    string EntityId,
    EntityIdentityQuality IdentityQuality,
    WorldProvenance Source,
    IReadOnlyList<WorldProvenance> EvidenceSources,
    double ObservedAtGameTime,
    DateTimeOffset ReceivedAtUtc,
    double AgeSeconds,
    WorldFreshness FreshnessClass,
    double Confidence,
    WorldPosition? Position,
    double? PositionErrorMeters);

public sealed record WorldMapState(
    WorldEntityMetadata Metadata,
    string Name,
    double SizeMeters,
    string Grid,
    double Daytime);

public sealed record WorldPlayerState(
    WorldEntityMetadata Metadata,
    string Side,
    WorldPosition? PositionAsl,
    double BodyHeading,
    double ViewHeading,
    double SpeedKph,
    double Damage,
    string LifeState,
    string Stance,
    string Weapon,
    string Magazine,
    string Muzzle,
    int LoadedRounds,
    int MatchingMagazineCount,
    int MatchingMagazineRounds);

public sealed record WorldGroupState(
    WorldEntityMetadata Metadata,
    string Side,
    string LocalLabel);

public sealed record WorldVehicleState(
    WorldEntityMetadata Metadata,
    string Class,
    string DisplayName,
    double Heading,
    double SpeedKph,
    double Fuel,
    double Damage,
    string Role);

public sealed record WorldKnownContactState(
    WorldEntityMetadata Metadata,
    string Alias,
    string Class,
    string DisplayName,
    bool KnownByPlayer,
    bool KnownByGroup,
    double? LastSeenAgeSeconds,
    double? LastThreatAgeSeconds,
    string PerceivedSide,
    bool Ignored,
    string TargetType,
    string Relationship,
    IReadOnlyList<string> Sensors);

public sealed record WorldStateView(
    long Version,
    bool IsConnected,
    bool HasTelemetry,
    string SessionId,
    int SessionOrdinal,
    WorldResetReason? LastResetReason,
    DateTimeOffset? SessionStartedAtUtc,
    DateTimeOffset? LastReceivedAtUtc,
    double LastObservedAtGameTime,
    long LastFrame,
    WorldMapState? Map,
    WorldPlayerState? Player,
    WorldGroupState? Group,
    WorldVehicleState? Vehicle,
    IReadOnlyList<WorldKnownContactState> KnownContacts);

public sealed record WorldStateDelta(
    long Version,
    bool ConnectionChanged,
    bool SessionReset,
    WorldResetReason? ResetReason,
    IReadOnlyList<string> UpdatedEntityIds);

public enum TelemetryIngestStatus
{
    Applied,
    Ignored,
    Rejected,
    OutOfOrder
}

public sealed record TelemetryIngestResult(
    TelemetryIngestStatus Status,
    bool SessionReset = false,
    WorldResetReason? ResetReason = null,
    int KnownContactCount = 0,
    int SkippedEntityCount = 0,
    string DiagnosticCode = "");
