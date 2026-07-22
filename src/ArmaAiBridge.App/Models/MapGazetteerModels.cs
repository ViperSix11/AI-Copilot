namespace ArmaAiBridge.App.Models;

public enum MapGazetteerReadiness
{
    Unavailable,
    Requesting,
    Assembling,
    Ready,
    Empty,
    Failed
}

public sealed record MapGazetteerLocation(
    string Key,
    string Name,
    string Type,
    double X,
    double Y,
    double RadiusA,
    double RadiusB,
    double Angle);

public sealed record MapGazetteerSnapshot(
    MapGazetteerReadiness Readiness,
    string WorldName,
    double WorldSizeMeters,
    IReadOnlyList<MapGazetteerLocation> Locations,
    string DiagnosticCode);

public sealed record MapGazetteerDiagnostics(
    MapGazetteerReadiness Readiness,
    string WorldName,
    int ReceivedPages,
    int ExpectedPages,
    int LocationCount,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string DiagnosticCode);

public sealed record InterpretedLocationReference(
    string Name,
    string Type,
    bool Inside,
    double DistanceMeters,
    int RoundedDistanceMeters,
    double? DistanceKlicks,
    int BearingFromReference,
    string DirectionFromReference);

public sealed record PositionInterpretation(
    string Status,
    string WorldName,
    string Grid,
    WorldPosition MeasuredPosition,
    double InformationAgeSeconds,
    InterpretedLocationReference? PrimaryReference,
    IReadOnlyList<InterpretedLocationReference> AlternativeReferences,
    string GazetteerReadiness);

public sealed record GazetteerIngestResult(bool Applied, bool Activated, string DiagnosticCode);
