namespace ArmaAiBridge.App.Models;

public enum MapKnowledgeReadiness
{
    Unavailable,
    Indexing,
    Partial,
    Ready,
    Stale,
    Failed
}

public sealed record MapAddonFingerprintInput(
    string Prefix,
    string Version,
    bool Patched,
    string Hash);

public sealed record MapGridReference(double X, double Y, string Label);

public sealed record MapWorldConfigFingerprintInput(
    string ClassName,
    string Description,
    double? MapSize,
    double? MapZone,
    double? Latitude,
    double? Longitude,
    IReadOnlyList<string> SourceAddons);

public sealed record MapProductFingerprintInput(
    string ShortName,
    double Version,
    long Build,
    string BuildType,
    string Platform,
    string Architecture,
    string Branch);

public sealed record MapExportSettings(
    int TileSizeMeters,
    int TerrainSampleSpacingMeters,
    int TotalTiles,
    int MaxRecordsPerPage);

public sealed record MapManifestData(
    string MissionId,
    string SessionId,
    int IndexVersion,
    string WorldName,
    double WorldSizeMeters,
    IReadOnlyList<double> TerrainInfo,
    MapWorldConfigFingerprintInput WorldConfig,
    IReadOnlyList<MapGridReference> GridReferences,
    MapProductFingerprintInput Product,
    IReadOnlyList<MapAddonFingerprintInput> Addons,
    MapExportSettings Export);

public sealed record MapTileBounds(
    int Ordinal,
    int Column,
    int Row,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY);

public sealed record MapTilePageData(
    string SessionId,
    string ExportId,
    string Fingerprint,
    int IndexVersion,
    MapTileBounds Tile,
    int PageIndex,
    int PageCount,
    string RecordsJson);

public sealed record MapLocationResult(
    string EntityId,
    string OfficialName,
    string LocationType,
    double X,
    double Y,
    double ZAsl,
    double DistanceMeters,
    double BearingDegrees);

public sealed record MapBuildingResult(
    string EntityId,
    string ClassName,
    string ModelName,
    string TerrainType,
    double X,
    double Y,
    double ZAsl,
    double DistanceMeters,
    double BearingDegrees);

public sealed record MapRoadResult(
    string EntityId,
    string RoadType,
    double WidthMeters,
    bool Pedestrian,
    bool Bridge,
    double BeginX,
    double BeginY,
    double EndX,
    double EndY,
    double DistanceMeters);

public sealed record MapIntersectionResult(
    string EntityId,
    double X,
    double Y,
    double ZAsl,
    int ConnectedSegments,
    double DistanceMeters);

public sealed record MapTerrainSummary(
    int SampleCount,
    double? MinimumElevationAsl,
    double? MaximumElevationAsl,
    double? AverageElevationAsl,
    double? AverageSlopeDegrees,
    double? MaximumSlopeDegrees);

public sealed record MapTileSummaryResult(
    int TileOrdinal,
    int TreeCount,
    int BushCount,
    int ForestCount,
    double VegetationDensityPerHectare,
    string WaterClassification,
    int WaterSamples,
    int LandSamples);

public sealed record MapKnowledgeDiagnostics(
    MapKnowledgeReadiness Readiness,
    string WorldName,
    string Fingerprint,
    string DatabasePath,
    int IndexVersion,
    int CompletedTiles,
    int TotalTiles,
    int Locations,
    int TerrainSamples,
    int Buildings,
    int RoadSegments,
    int RoadIntersections,
    int TileSummaries,
    string LastError,
    bool ExportActive,
    int PendingTilePages)
{
    public double ProgressPercent => TotalTiles <= 0
        ? 0
        : Math.Clamp(CompletedTiles * 100.0 / TotalTiles, 0, 100);
}
