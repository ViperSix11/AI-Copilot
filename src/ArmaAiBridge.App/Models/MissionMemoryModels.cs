namespace ArmaAiBridge.App.Models;

public sealed record MissionContactTrack(
    string TrackId,
    string ContactType,
    string Description,
    string PerceivedSide,
    string Relationship,
    string Status,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset LastThreatAtUtc,
    WorldPosition EstimatedPosition,
    double UncertaintyRadiusMeters,
    int ObservationCount,
    bool Corroborated,
    IReadOnlyList<string> ReporterCallsigns);

public sealed record MissionContactObservation(
    long ObservationId,
    string TrackId,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ReceivedAtUtc,
    WorldPosition EstimatedPosition,
    double UncertaintyRadiusMeters,
    WorldPosition? PlayerPosition);

public sealed record MissionMemoryEntry(
    long Id,
    string Text,
    string Provenance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<string> Tags,
    WorldPosition? Position);

public sealed record LoreSection(
    string Scope,
    string Content,
    bool Enabled,
    bool AlwaysInclude,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReportedLocationAnchor(
    string Key,
    string Label,
    string Grid,
    WorldPosition Position,
    double UncertaintyRadiusMeters,
    DateTimeOffset ReportedAtUtc);
