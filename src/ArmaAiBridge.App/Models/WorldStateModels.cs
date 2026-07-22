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
    FrameRegressed,
    ProtocolSessionChanged
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

public sealed record WorldProtocolFeatureState(string Name, int Version);

public sealed record WorldProtocolState(
    int Major,
    int Minor,
    string ViewerSide,
    string Visibility,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<WorldProtocolFeatureState> Features);

public sealed record WorldFriendlyGroupState(
    WorldEntityMetadata Metadata,
    string Alias,
    string Callsign,
    string Side,
    string LeaderAlias,
    IReadOnlyList<string> UnitAliases,
    string Behaviour);

public sealed record WorldFriendlyUnitState(
    WorldEntityMetadata Metadata,
    string Alias,
    string GroupAlias,
    string Callsign,
    string Side,
    string Class,
    string Role,
    bool Alive,
    string LifeState,
    bool Mobile,
    double Damage,
    string VehicleAlias,
    string VehicleRole,
    string MedicalReadiness);

public sealed record WorldFriendlyVehicleState(
    WorldEntityMetadata Metadata,
    string Alias,
    string Side,
    string Class,
    string DisplayName,
    bool Alive,
    bool Mobile,
    double Damage,
    double Fuel,
    double SpeedKph,
    IReadOnlyList<string> CrewUnitAliases,
    int CargoCapacity,
    int EmptyCargoSeats);

public sealed record WorldSupportAssetState(
    WorldEntityMetadata Metadata,
    string Alias,
    string Kind,
    string Callsign,
    string Provider,
    string VehicleAlias,
    string Status,
    bool Available,
    int Capacity);

public sealed record WorldCapabilityConstraints(
    int MaxConcurrent,
    IReadOnlyList<string> AllowedRequesterSides,
    double MaxRangeMeters,
    int MaxPassengers,
    bool SupportsCasualties,
    bool RequiresConfirmation);

public sealed record WorldCapabilityState(
    WorldEntityMetadata Metadata,
    string Alias,
    string Capability,
    bool Enabled,
    string Provider,
    WorldCapabilityConstraints Constraints);

public sealed record WorldReconciliationState(
    bool HasHandshake,
    bool HasCompleteReconciliation,
    string LastReconciliationId,
    DateTimeOffset? LastReconciledAtUtc,
    long LastSequence,
    bool SequenceGap,
    int PendingPageCount,
    bool IsDegraded,
    string DiagnosticCode,
    int CapabilityRegistryVersion);

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
    IReadOnlyList<WorldKnownContactState> KnownContacts,
    WorldProtocolState? Protocol,
    IReadOnlyList<WorldFriendlyGroupState> FriendlyGroups,
    IReadOnlyList<WorldFriendlyUnitState> FriendlyUnits,
    IReadOnlyList<WorldFriendlyVehicleState> FriendlyVehicles,
    IReadOnlyList<WorldSupportAssetState> SupportAssets,
    IReadOnlyList<WorldCapabilityState> Capabilities,
    WorldReconciliationState Reconciliation);

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
