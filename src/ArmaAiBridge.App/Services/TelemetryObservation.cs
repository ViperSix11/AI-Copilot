using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

internal sealed record TelemetryObservation(
    string MissionId,
    string SessionId,
    double GameTime,
    long Frame,
    DateTimeOffset ReceivedAtUtc,
    MapObservation Map,
    PlayerObservation Player,
    VehicleObservation? Vehicle,
    IReadOnlyList<ContactObservation> Contacts,
    IReadOnlyList<SensorContactObservation> SensorContacts,
    int SkippedEntityCount);

internal sealed record MapObservation(string Name, double SizeMeters, string Grid, double Daytime);

internal sealed record PlayerObservation(
    string SourceId,
    string Side,
    string GroupLabel,
    string GroupSourceId,
    WorldPosition PositionAtl,
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

internal sealed record VehicleObservation(
    string SourceId,
    string Class,
    string DisplayName,
    WorldPosition? PositionAtl,
    double Heading,
    double SpeedKph,
    double Fuel,
    double Damage,
    string Role);

internal sealed record ContactObservation(
    string OpaqueId,
    string Class,
    string DisplayName,
    bool KnownByPlayer,
    bool KnownByGroup,
    double? LastSeenAgeSeconds,
    double? LastThreatAgeSeconds,
    string PerceivedSide,
    double? PositionErrorMeters,
    WorldPosition? EstimatedPosition,
    bool Ignored,
    string Signature);

internal sealed record SensorContactObservation(
    string OpaqueId,
    string Class,
    string TargetType,
    string Relationship,
    IReadOnlyList<string> Sensors);

internal sealed record ProtocolEnvelope(
    string MessageId,
    string MissionId,
    string SessionId,
    double GameTime,
    long Sequence,
    DateTimeOffset ReceivedAtUtc);

internal sealed record SessionHandshakeObservation(
    ProtocolEnvelope Envelope,
    int ProtocolMajor,
    int ProtocolMinor,
    string WorldName,
    double WorldSizeMeters,
    string ViewerSide,
    string Visibility,
    IReadOnlyList<WorldProtocolFeatureState> Features);

internal sealed record FriendlyGroupObservation(
    string SourceId,
    string Callsign,
    string Side,
    string LeaderSourceId,
    IReadOnlyList<string> UnitSourceIds,
    WorldPosition PositionAtl,
    string Behaviour);

internal sealed record FriendlyUnitObservation(
    string SourceId,
    string GroupSourceId,
    string Callsign,
    string Side,
    string Class,
    string Role,
    WorldPosition PositionAtl,
    bool Alive,
    string LifeState,
    bool Mobile,
    double Damage,
    string VehicleSourceId,
    string VehicleRole,
    string MedicalReadiness);

internal sealed record FriendlyVehicleObservation(
    string SourceId,
    string Side,
    string Class,
    string DisplayName,
    WorldPosition PositionAtl,
    bool Alive,
    bool Mobile,
    double Damage,
    double Fuel,
    double SpeedKph,
    IReadOnlyList<string> CrewUnitSourceIds,
    int CargoCapacity,
    int EmptyCargoSeats);

internal sealed record SupportAssetObservation(
    string SourceId,
    string Kind,
    string Callsign,
    string Provider,
    string VehicleSourceId,
    string Status,
    bool Available,
    int Capacity);

internal sealed record FriendlyForceSnapshotPageObservation(
    ProtocolEnvelope Envelope,
    string ReconciliationId,
    int PageIndex,
    int PageCount,
    IReadOnlyList<FriendlyGroupObservation> Groups,
    IReadOnlyList<FriendlyUnitObservation> Units,
    IReadOnlyList<FriendlyVehicleObservation> Vehicles,
    IReadOnlyList<SupportAssetObservation> Assets);

internal sealed record FriendlyForceSnapshotObservation(
    ProtocolEnvelope Envelope,
    string ReconciliationId,
    IReadOnlyList<FriendlyGroupObservation> Groups,
    IReadOnlyList<FriendlyUnitObservation> Units,
    IReadOnlyList<FriendlyVehicleObservation> Vehicles,
    IReadOnlyList<SupportAssetObservation> Assets);

internal sealed record FriendlyForceDeltaObservation(
    ProtocolEnvelope Envelope,
    string BaseReconciliationId,
    IReadOnlyList<FriendlyGroupObservation> UpsertGroups,
    IReadOnlyList<FriendlyUnitObservation> UpsertUnits,
    IReadOnlyList<FriendlyVehicleObservation> UpsertVehicles,
    IReadOnlyList<SupportAssetObservation> UpsertAssets,
    IReadOnlyList<string> RemovedGroupIds,
    IReadOnlyList<string> RemovedUnitIds,
    IReadOnlyList<string> RemovedVehicleIds,
    IReadOnlyList<string> RemovedAssetIds);

internal sealed record CapabilityConstraintsObservation(
    int MaxConcurrent,
    IReadOnlyList<string> AllowedRequesterSides,
    double MaxRangeMeters,
    int MaxPassengers,
    bool SupportsCasualties,
    bool RequiresConfirmation);

internal sealed record CapabilityObservation(
    string SourceId,
    string Capability,
    bool Enabled,
    string Provider,
    CapabilityConstraintsObservation Constraints);

internal sealed record MissionCapabilitiesObservation(
    ProtocolEnvelope Envelope,
    int RegistryVersion,
    IReadOnlyList<CapabilityObservation> Capabilities);
