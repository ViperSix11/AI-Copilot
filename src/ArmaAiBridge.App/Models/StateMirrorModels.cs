using System.Text.Json;

namespace ArmaAiBridge.App.Models;

public enum StateSectionReadiness
{
    Ready,
    Stale,
    Unavailable,
    Failed
}

public enum StateRepositoryReadiness
{
    Unavailable,
    Ready,
    Failed
}

public sealed record StateSectionMetadata(
    string Section,
    StateSectionReadiness Readiness,
    double SampledAtGameTime,
    DateTimeOffset ReceivedAtUtc,
    double AgeSeconds,
    bool IsStale);

public sealed record StatePlayer(
    string Alias,
    string Side,
    string GroupAlias,
    string GroupCallsign,
    WorldPosition PositionAtl,
    WorldPosition PositionAsl,
    string Grid,
    StateSectionMetadata Metadata);

public sealed record StateEnvironment(
    double Overcast,
    double ForecastOvercast,
    double Rain,
    double Fog,
    IReadOnlyList<double> FogParameters,
    double ForecastFog,
    double WindX,
    double WindY,
    double WindDirection,
    double WindStrength,
    double Gusts,
    double Waves,
    double Lightning,
    double Humidity,
    double? TemperatureCelsius,
    double NextWeatherChange,
    StateSectionMetadata Metadata);

public sealed record StateTimeAstronomy(
    IReadOnlyList<int> MissionDate,
    double Daytime,
    double ElapsedMissionTime,
    double TimeMultiplier,
    double MoonPhase,
    double SunOrMoon,
    StateSectionMetadata Metadata);

public sealed record StateMagazine(string Class, string DisplayName, int Rounds, bool Loaded, string Container);
public sealed record StateMagazineTotal(string Class, string DisplayName, int MagazineCount, int Rounds);

public sealed record StateLoadout(
    string PrimaryWeapon,
    string Launcher,
    string Handgun,
    string SelectedWeapon,
    string SelectedWeaponDisplayName,
    string Muzzle,
    string FireMode,
    string CurrentMagazine,
    int LoadedRounds,
    IReadOnlyList<string> OpticsAndAttachments,
    string Binocular,
    IReadOnlyList<StateMagazine> Magazines,
    IReadOnlyList<StateMagazineTotal> MagazineTotals,
    int GrenadeCount,
    int ThrowableCount,
    int MineCount,
    int ExplosiveCount,
    IReadOnlyList<string> AssignedItems,
    string UniformClass,
    string VestClass,
    string BackpackClass,
    string LoadoutHash,
    StateSectionMetadata Metadata);

public sealed record StateWaypoint(int Index, WorldPosition Position, string Type);

public sealed record StateFriendlyGroup(
    string Alias,
    string Callsign,
    string LeaderAlias,
    IReadOnlyList<string> MemberAliases,
    WorldPosition LeaderPosition,
    string Behaviour,
    string CombatMode,
    string Formation,
    StateWaypoint? Waypoint,
    WorldPosition? ExpectedDestination,
    IReadOnlyList<string> AssignedTargetAliases,
    StateSectionMetadata Metadata);

public sealed record StateFriendlyUnit(
    string Alias,
    string GroupAlias,
    string Class,
    string DisplayRole,
    WorldPosition Position,
    bool Alive,
    string LifeState,
    bool Mobile,
    double Damage,
    string CurrentCommand,
    string AssignedTargetAlias,
    string VehicleAlias,
    string VehicleRole,
    StateSectionMetadata Metadata);

public sealed record StateKnownContact(
    string Alias,
    string Class,
    string DisplayName,
    string ContactType,
    string PerceivedSide,
    string Relationship,
    WorldPosition EstimatedPosition,
    double PositionErrorMeters,
    double LastSeenAgeSeconds,
    double LastThreatAgeSeconds,
    IReadOnlyList<string> ObserverGroupAliases,
    StateSectionMetadata Metadata);

public sealed record StateTask(
    string Alias,
    string Title,
    string Description,
    WorldPosition? Destination,
    string Type,
    string Status,
    string ParentAlias,
    bool Active,
    StateSectionMetadata Metadata);

public sealed record StateMarker(
    string Alias,
    string Text,
    WorldPosition Position,
    string Type,
    string Color,
    string Shape,
    IReadOnlyList<double> Size,
    double Direction,
    double Alpha,
    IReadOnlyList<double> Polyline,
    StateSectionMetadata Metadata);

public sealed record StateSnapshotSection(
    string Name,
    StateSectionReadiness Readiness,
    double SampledAtGameTime,
    JsonElement Payload);

public sealed record StateSnapshotMessage(
    string MessageId,
    string MissionId,
    string SessionId,
    double PublishedAtGameTime,
    long Sequence,
    bool FullReconciliation,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyDictionary<string, StateSnapshotSection> Sections);

public sealed record StateRepositoryDiagnostics(
    StateRepositoryReadiness Readiness,
    string ActiveSessionAlias,
    string WorldName,
    bool BaselineReady,
    long LastSequence,
    DateTimeOffset? LastSnapshotReceivedAtUtc,
    IReadOnlyList<StateSectionMetadata> Sections,
    IReadOnlyDictionary<string, int> RowCounts,
    long DatabaseSizeBytes,
    int SchemaVersion,
    int ProtocolVersion,
    string DiagnosticCode);

public sealed record StateIngestResult(TelemetryIngestStatus Status, string DiagnosticCode = "");
