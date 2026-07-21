using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

internal sealed record TelemetryObservation(
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
    string Side,
    string GroupLabel,
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
