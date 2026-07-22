namespace ArmaAiBridge.App.Models;

public sealed record EnvironmentInterpretation(
    double WindSpeedMetersPerSecond,
    int WindBearingDegrees,
    string WindCardinalDirection,
    string RainClassification,
    string FogClassification,
    double? TemperatureCelsius,
    string DaytimeDescription,
    double AgeSeconds,
    bool IsStale);

public sealed record LoadoutSummary(
    string CurrentWeapon,
    string CurrentWeaponDisplayName,
    int LoadedRounds,
    int ReserveMagazines,
    int ReserveRounds,
    int Grenades,
    int Throwables,
    int Mines,
    int Explosives,
    IReadOnlyList<string> OpticsAndAttachments,
    double AgeSeconds,
    bool IsStale);

public sealed record ForceSummary(
    int GroupCount,
    int UnitCount,
    int WoundedCount,
    int IncapacitatedCount,
    int DeadCount,
    double AgeSeconds,
    bool IsStale);

public sealed record ContactSummary(
    int KnownContactCount,
    IReadOnlyDictionary<string, int> ByPerceivedSide,
    IReadOnlyDictionary<string, int> ByBroadType,
    double? NewestContactAgeSeconds,
    double? MaximumPositionUncertaintyMeters,
    int StaleContactCount,
    double AgeSeconds,
    bool IsStale);
