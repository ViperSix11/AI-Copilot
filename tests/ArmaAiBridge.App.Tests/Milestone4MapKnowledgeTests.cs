using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Milestone4MapKnowledgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aab-m4-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("map-manifest-v1")]
    [InlineData("map-tile-v1")]
    [InlineData("map-index-progress-v1")]
    [InlineData("map-index-command-v1")]
    public void ProtocolFixtures_MatchClosedVersionedSchemas(string contract)
    {
        using JsonDocument schema = JsonDocument.Parse(Fixture($"schemas/{contract}.schema.json"));
        using JsonDocument fixture = JsonDocument.Parse(Fixture($"{contract}.json"));
        JsonElement schemaRoot = schema.RootElement, fixtureRoot = fixture.RootElement;
        Assert.False(schemaRoot.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(schemaRoot.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString(),
            fixtureRoot.GetProperty("schema").GetString());
        foreach (JsonElement required in schemaRoot.GetProperty("required").EnumerateArray())
            Assert.True(fixtureRoot.TryGetProperty(required.GetString()!, out _), $"Missing {required} in {contract}.");
        foreach (JsonProperty property in fixtureRoot.EnumerateObject())
            Assert.True(schemaRoot.GetProperty("properties").TryGetProperty(property.Name, out _), $"Unexpected {property.Name}.");
        AssertClosedSchemaObjects(schemaRoot);
    }

    [Fact]
    public void Fingerprint_IsCanonicalAndChangesForMaterialInputs()
    {
        MapManifestData manifest = Manifest();
        string first = MapFingerprint.Compute(manifest);
        MapManifestData reordered = manifest with
        {
            WorldName = "  STRATIS ",
            Addons = manifest.Addons.Reverse().ToArray(),
            WorldConfig = manifest.WorldConfig with { SourceAddons = manifest.WorldConfig.SourceAddons.Reverse().ToArray() }
        };
        Assert.Equal(first, MapFingerprint.Compute(reordered));
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.NotEqual(first, MapFingerprint.Compute(manifest with { WorldSizeMeters = manifest.WorldSizeMeters + 1 }));
        Assert.NotEqual(first, MapFingerprint.Compute(manifest with
        {
            Addons = manifest.Addons.Select(addon => addon with { Hash = addon.Hash + "changed" }).ToArray()
        }));
        Assert.NotEqual(first, MapFingerprint.Compute(manifest with
        {
            Product = manifest.Product with { Build = manifest.Product.Build + 1 }
        }));
    }

    [Fact]
    public void Migration_CreatesRtreesAndIsIdempotent()
    {
        MapManifestData manifest = Manifest();
        MapKnowledgeDatabase database = new(_root);
        string fingerprint = MapFingerprint.Compute(manifest);
        database.Activate(manifest, fingerprint);
        database.Activate(manifest, fingerprint);

        using SqliteConnection connection = new($"Data Source={database.DatabasePath}");
        connection.Open();
        Assert.Equal(1L, Scalar(connection, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM schema_migrations"));
        foreach (string table in new[] { "location_rtree", "terrain_rtree", "building_rtree", "road_rtree", "intersection_rtree", "tile_rtree" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            command.Parameters.AddWithValue("@name", table);
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
        Assert.True(File.Exists(Path.Combine(_root, "cache-manifest-v1.json")));
    }

    [Fact]
    public void Migration_RejectsUnknownNewerDatabaseVersion()
    {
        MapManifestData manifest = Manifest();
        string fingerprint = MapFingerprint.Compute(manifest);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, fingerprint + ".sqlite3");
        using (SqliteConnection connection = new($"Data Source={path}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery();
        }
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new MapKnowledgeDatabase(_root).Activate(manifest, fingerprint));
        Assert.Equal("database_version_newer", error.Message);
    }

    [Fact]
    public void FingerprintChange_MarksPriorCacheStaleAndPreservesCatalogEntry()
    {
        (MapKnowledgeDatabase _, MapManifestData original) = ReadyDatabase();
        string oldFingerprint = MapFingerprint.Compute(original);
        MapManifestData changed = original with { Product = original.Product with { Build = original.Product.Build + 1 } };
        string newFingerprint = MapFingerprint.Compute(changed);

        MapKnowledgeDatabase replacement = new(_root);
        replacement.Activate(changed, newFingerprint);

        using (SqliteConnection connection = new($"Data Source={Path.Combine(_root, oldFingerprint + ".sqlite3")};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT readiness FROM map_manifest WHERE id=1";
            Assert.Equal("stale", command.ExecuteScalar());
        }
        using JsonDocument catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "cache-manifest-v1.json")));
        Assert.Equal(newFingerprint, catalog.RootElement.GetProperty("currentFingerprint").GetString());
        JsonElement[] entries = catalog.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, entry => entry.GetProperty("fingerprint").GetString() == oldFingerprint && entry.GetProperty("readiness").GetString() == "stale");
    }

    [Fact]
    public void InvalidLocalCatalog_IsAtomicallyRecoveredFromDatabaseState()
    {
        MapManifestData manifest = Manifest();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "cache-manifest-v1.json"), "{\"entries\":\"corrupt\"}");

        MapKnowledgeDatabase database = new(_root);
        database.Activate(manifest, MapFingerprint.Compute(manifest));

        using JsonDocument recovered = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "cache-manifest-v1.json")));
        Assert.Equal("arma-ai-bridge/map-cache-manifest-v1", recovered.RootElement.GetProperty("schema").GetString());
        Assert.Single(recovered.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task CorruptDatabase_ReportsFailedWithoutLeakingSqliteDetails()
    {
        MapManifestData manifest = Manifest();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, MapFingerprint.Compute(manifest) + ".sqlite3"), "not a sqlite database");
        MapKnowledgeDatabase database = new(_root);
        using MapKnowledgeService service = new(database);

        await service.ProcessMessageAsync(Handshake(), TestContext.Current.CancellationToken);
        await service.ProcessMessageAsync(Fixture("map-manifest-v1.json"), TestContext.Current.CancellationToken);

        MapKnowledgeDiagnostics diagnostics = service.GetDiagnostics();
        Assert.Equal(MapKnowledgeReadiness.Failed, diagnostics.Readiness);
        Assert.Equal("sqlite_failure", diagnostics.LastError);
        Assert.DoesNotContain("not a sqlite", JsonSerializer.Serialize(diagnostics), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TileProtocol_RejectsUnknownPropertiesAndOversizedPages()
    {
        JsonNode unexpected = JsonNode.Parse(Fixture("map-tile-v1.json"))!;
        unexpected["records"]![0]!["rawEngineId"] = "forbidden";
        using JsonDocument unexpectedDocument = JsonDocument.Parse(unexpected.ToJsonString());
        Assert.Equal("unexpected_property",
            Assert.Throws<InvalidDataException>(() => MapKnowledgeProtocol.ParseTilePage(unexpectedDocument.RootElement)).Message);

        JsonNode oversized = JsonNode.Parse(Fixture("map-tile-v1.json"))!;
        JsonArray records = oversized["records"]!.AsArray();
        while (records.Count <= 96) records.Add(records[0]!.DeepClone());
        using JsonDocument oversizedDocument = JsonDocument.Parse(oversized.ToJsonString());
        Assert.Equal("page_record_limit_exceeded",
            Assert.Throws<InvalidDataException>(() => MapKnowledgeProtocol.ParseTilePage(oversizedDocument.RootElement)).Message);
    }

    [Theory]
    [InlineData("sessionId", "different-session", "session_mismatch")]
    [InlineData("fingerprint", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "fingerprint_mismatch")]
    public async Task TileProtocol_RejectsCrossSessionAndWrongFingerprint(string field, string value, string expectedError)
    {
        string? commandJson = null;
        MapKnowledgeDatabase database = new(_root);
        using MapKnowledgeService service = new(database, sendCommand: (json, _) => { commandJson = json; return Task.FromResult(true); });
        await service.ProcessMessageAsync(Handshake(), TestContext.Current.CancellationToken);
        await service.ProcessMessageAsync(Fixture("map-manifest-v1.json"), TestContext.Current.CancellationToken);
        using JsonDocument command = JsonDocument.Parse(commandJson!);
        JsonNode page = TileNode(
            command.RootElement.GetProperty("parameters").GetProperty("exportId").GetString()!,
            command.RootElement.GetProperty("parameters").GetProperty("fingerprint").GetString()!);
        page[field] = value;

        await service.ProcessMessageAsync(page.ToJsonString(), TestContext.Current.CancellationToken);

        Assert.Equal(MapKnowledgeReadiness.Failed, database.GetReadiness());
        Assert.Equal(expectedError, database.GetDiagnostics().LastError);
        Assert.Equal(0, database.GetDiagnostics().CompletedTiles);
    }

    [Fact]
    public void CompleteTiles_SupportDeterministicSpatialAndNameQueries()
    {
        (MapKnowledgeDatabase database, MapManifestData manifest) = ReadyDatabase();
        IReadOnlyList<MapLocationResult> nearest = database.FindNearestLocations(0, 0, 500, Array.Empty<string>(), 10);
        Assert.Single(nearest);
        Assert.Equal("Camp Maxwell", nearest[0].OfficialName);
        Assert.Equal(Math.Sqrt(20000), nearest[0].DistanceMeters, 6);
        Assert.Equal(45, nearest[0].BearingDegrees, 6);
        Assert.Single(database.FindNearestLocations(0, 0, 500, new[] { "namelocal" }, 10));
        Assert.Single(database.FindLocationsByName("max", 10, 0, 0));
        Assert.Single(database.QueryBuildings(0, 0, 500, 10));
        Assert.Equal(2, database.QueryRoads(160, 80, 200, 10).Count);
        MapIntersectionResult intersection = Assert.Single(database.QueryIntersections(160, 80, 20, 10));
        Assert.Equal(2, intersection.ConnectedSegments);
        Assert.Equal(1, database.QueryTerrain(0, 0, 500).SampleCount);
        Assert.Contains(database.QueryTileSummaries(100, 100, 300, 10), value => value.TreeCount == 20 && value.WaterClassification == "land");
        Assert.Equal(MapKnowledgeReadiness.Ready, database.GetReadiness());
        Assert.Equal(manifest.Export.TotalTiles, database.GetFirstMissingTile());
    }

    [Fact]
    public async Task TilePages_AreAtomicAndResumeFromFirstMissingTile()
    {
        List<JsonElement> commands = new();
        MapKnowledgeDatabase database = new(_root);
        using MapKnowledgeService service = new(database, sendCommand: (json, _) =>
        {
            commands.Add(JsonDocument.Parse(json).RootElement.Clone());
            return Task.FromResult(true);
        });
        await service.ProcessMessageAsync(Handshake(), TestContext.Current.CancellationToken);
        await service.ProcessMessageAsync(Fixture("map-manifest-v1.json"), TestContext.Current.CancellationToken);
        Assert.Single(commands);
        string exportId = commands[0].GetProperty("parameters").GetProperty("exportId").GetString()!;
        string fingerprint = commands[0].GetProperty("parameters").GetProperty("fingerprint").GetString()!;
        JsonNode page0 = TileNode(exportId, fingerprint);
        page0["pageCount"] = 2;
        JsonArray records = page0["records"]!.AsArray();
        JsonArray secondRecords = new();
        while (records.Count > 3)
        {
            JsonNode value = records[3]!.DeepClone(); records.RemoveAt(3); secondRecords.Add(value);
        }
        JsonNode page1 = page0.DeepClone(); page1["pageIndex"] = 1; page1["records"] = secondRecords;
        await service.ProcessMessageAsync(page0.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.Equal(0, database.GetDiagnostics().CompletedTiles);
        await service.ProcessMessageAsync(page1.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.Equal(1, database.GetDiagnostics().CompletedTiles);
        Assert.Equal(1, database.GetFirstMissingTile());
        Assert.Equal(MapKnowledgeReadiness.Indexing, database.GetReadiness());
    }

    [Fact]
    public async Task ConflictingDuplicatePage_FailsClosedWithoutCompletingTile()
    {
        string? commandJson = null;
        MapKnowledgeDatabase database = new(_root);
        using MapKnowledgeService service = new(database, sendCommand: (json, _) => { commandJson = json; return Task.FromResult(true); });
        await service.ProcessMessageAsync(Handshake(), TestContext.Current.CancellationToken);
        await service.ProcessMessageAsync(Fixture("map-manifest-v1.json"), TestContext.Current.CancellationToken);
        using JsonDocument command = JsonDocument.Parse(commandJson!);
        string export = command.RootElement.GetProperty("parameters").GetProperty("exportId").GetString()!;
        string fingerprint = command.RootElement.GetProperty("parameters").GetProperty("fingerprint").GetString()!;
        JsonNode page = TileNode(export, fingerprint); page["pageCount"] = 2;
        await service.ProcessMessageAsync(page.ToJsonString(), TestContext.Current.CancellationToken);
        page["records"]![0]!["name"] = "Changed";
        await service.ProcessMessageAsync(page.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.Equal(MapKnowledgeReadiness.Failed, database.GetReadiness());
        Assert.Equal(0, database.GetDiagnostics().CompletedTiles);
        Assert.Equal("conflicting_tile_page", database.GetDiagnostics().LastError);
    }

    [Fact]
    public async Task ReadyMatchingCache_IsReusedWithoutStartingExporter()
    {
        (MapKnowledgeDatabase _, MapManifestData manifest) = ReadyDatabase();
        int sends = 0;
        using MapKnowledgeService restarted = new(new MapKnowledgeDatabase(_root), sendCommand: (_, _) => { sends++; return Task.FromResult(true); });
        await restarted.ProcessMessageAsync(Handshake(), TestContext.Current.CancellationToken);
        await restarted.ProcessMessageAsync(Fixture("map-manifest-v1.json"), TestContext.Current.CancellationToken);
        Assert.Equal(0, sends);
        Assert.Equal(MapKnowledgeReadiness.Ready, restarted.GetDiagnostics().Readiness);
        Assert.Equal(MapFingerprint.Compute(manifest), restarted.GetDiagnostics().Fingerprint);
    }

    [Fact]
    public void SnapshotBuilders_ReturnOnlyBoundedRetrievedDataAndPrivacySafeMetadata()
    {
        (MapKnowledgeDatabase database, _) = ReadyDatabase();
        ManualTimeProvider time = new(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        WorldStateStore world = new(time);
        using TelemetryIngestService ingest = new(world, time);
        JsonNode telemetry = JsonNode.Parse(WorldModelTestData.Telemetry())!;
        telemetry["player"]!["positionATL"] = new JsonArray(0.0, 0.0, 0.0);
        telemetry["player"]!["positionASL"] = new JsonArray(0.0, 0.0, 0.0);
        ingest.Ingest(telemetry.ToJsonString());
        using MapKnowledgeService service = new(database);
        MapKnowledgeSnapshotBuilder snapshots = new(service, world);
        string nearest = snapshots.FindNearestLocations(Arguments("""{"maxDistanceMeters":500,"locationTypes":[],"limit":10}"""));
        string named = snapshots.FindLocationsByName(Arguments("""{"name":"max","limit":10}"""));
        string area = snapshots.QueryMapArea(Arguments("""{"radiusMeters":500,"categories":["location","terrain","building","road","vegetation","water"],"limitPerCategory":10}"""));
        Assert.Contains("Camp Maxwell", nearest, StringComparison.Ordinal);
        Assert.Contains("Camp Maxwell", named, StringComparison.Ordinal);
        Assert.Contains("road", area, StringComparison.OrdinalIgnoreCase);
        foreach (string forbidden in new[] { database.DatabasePath, "a3\\map_stratis", "abc123", "canonical_json", "addons", "raw", "netId", "profileName", "uid" })
        {
            Assert.DoesNotContain(forbidden, nearest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, named, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, area, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(nearest.Length < 64 * 1024);
        Assert.True(area.Length < 64 * 1024);
    }

    [Fact]
    public void SqfContract_UsesTimeSlicedReadOnlyCollectionAndNoExpandedCapabilities()
    {
        using JsonDocument contract = JsonDocument.Parse(Fixture("sqf-milestone-4-contract-v1.json"));
        string repository = RepositoryRoot();
        foreach (JsonElement file in contract.RootElement.GetProperty("files").EnumerateArray())
        {
            string path = file.GetProperty("path").GetString()!;
            string contents = File.ReadAllText(Path.Combine(repository, path));
            foreach (JsonElement token in file.GetProperty("requiredTokens").EnumerateArray())
                Assert.Contains(token.GetString()!, contents, StringComparison.Ordinal);
            foreach (JsonElement token in file.GetProperty("forbiddenTokens").EnumerateArray())
                Assert.DoesNotContain(token.GetString()!, contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    private (MapKnowledgeDatabase Database, MapManifestData Manifest) ReadyDatabase()
    {
        MapManifestData manifest = Manifest();
        MapKnowledgeDatabase database = new(_root);
        database.Activate(manifest, MapFingerprint.Compute(manifest));
        using JsonDocument tile = JsonDocument.Parse(Fixture("map-tile-v1.json"));
        database.CommitTile(new(0, 0, 0, 0, 0, 512, 512), new[] { tile.RootElement.GetProperty("records").GetRawText() });
        database.CommitTile(new(1, 1, 0, 512, 0, 1024, 512), new[] { "[]" });
        database.CommitTile(new(2, 0, 1, 0, 512, 512, 1024), new[] { "[]" });
        database.CommitTile(new(3, 1, 1, 512, 512, 1024, 1024), new[] { "[]" });
        database.CompleteIndex();
        return (database, manifest);
    }

    private static MapManifestData Manifest()
    {
        using JsonDocument document = JsonDocument.Parse(Fixture("map-manifest-v1.json"));
        return MapKnowledgeProtocol.ParseManifest(document.RootElement);
    }

    private static JsonNode TileNode(string exportId, string fingerprint)
    {
        JsonNode node = JsonNode.Parse(Fixture("map-tile-v1.json"))!;
        node["exportId"] = exportId; node["fingerprint"] = fingerprint; return node;
    }

    private static string Handshake() => """
        {"schema":"arma-ai-bridge/arma3/session-handshake-v1","sessionId":"session-test","features":[{"name":"static-map-export","version":1}]}
        """;

    private static JsonElement Arguments(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static long Scalar(SqliteConnection connection, string sql) { using SqliteCommand command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
    private static void AssertClosedSchemaObjects(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object" &&
                node.TryGetProperty("properties", out _))
                Assert.True(node.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.False,
                    "Every object schema with declared properties must reject additional properties.");
            foreach (JsonProperty property in node.EnumerateObject()) AssertClosedSchemaObjects(property.Value);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement value in node.EnumerateArray()) AssertClosedSchemaObjects(value);
        }
    }
    private static string Fixture(string relativePath) => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", relativePath)));
    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
