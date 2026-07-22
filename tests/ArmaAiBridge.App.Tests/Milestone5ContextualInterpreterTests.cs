using System.Diagnostics;
using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Milestone5ContextualInterpreterTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Gazetteer_ActivatesOnlyAfterEveryValidatedPageArrives()
    {
        MapGazetteerStore store = ReadyRequest();
        GazetteerIngestResult second = store.Ingest(Page(1, 2, 2,
            Location("two", "Camp Tempest", "NameLocal", 4000, 6000)));

        Assert.True(second.Applied);
        Assert.False(second.Activated);
        Assert.Equal(MapGazetteerReadiness.Assembling, store.GetSnapshot().Readiness);
        Assert.Empty(store.GetSnapshot().Locations);

        GazetteerIngestResult first = store.Ingest(Page(0, 2, 2,
            Location("one", "Agia Marina", "NameCity", 3400, 5600)));

        Assert.True(first.Activated);
        Assert.Equal(MapGazetteerReadiness.Ready, store.GetSnapshot().Readiness);
        Assert.Equal(new[] { "one", "two" }, store.GetSnapshot().Locations.Select(item => item.Key));
    }

    [Fact]
    public void Gazetteer_DuplicateMismatchAndInvalidNumbersFailClosed()
    {
        MapGazetteerStore duplicate = ReadyRequest();
        duplicate.Ingest(Page(0, 2, 2, Location("one", "Agia Marina", "NameCity", 3400, 5600)));
        Assert.Equal("gazetteer_duplicate_page", duplicate.Ingest(
            Page(0, 2, 2, Location("one", "Agia Marina", "NameCity", 3400, 5600))).DiagnosticCode);
        Assert.Equal(MapGazetteerReadiness.Failed, duplicate.GetSnapshot().Readiness);

        MapGazetteerStore mismatch = ReadyRequest();
        Assert.Equal("gazetteer_identity_mismatch", mismatch.Ingest(
            Page(0, 1, 1, new[] { Location("one", "Agia Marina", "NameCity", 3400, 5600) }, "wrong")).DiagnosticCode);

        MapGazetteerStore invalid = ReadyRequest();
        string invalidPage = Page(0, 1, 1, Location("one", "Agia Marina", "NameCity", 3400, 5600))
            .Replace("\"radiusA\":100", "\"radiusA\":-1", StringComparison.Ordinal);
        Assert.Equal("gazetteer_radiusA_invalid", invalid.Ingest(invalidPage).DiagnosticCode);
        Assert.Empty(invalid.GetSnapshot().Locations);
    }

    [Fact]
    public void Gazetteer_EmptyResetAndIncompleteBatchHaveExplicitStates()
    {
        ManualTimeProvider time = new(Start);
        MapGazetteerStore store = ReadyRequest(time);
        Assert.True(store.Ingest(Page(0, 1, 0)).Activated);
        Assert.Equal(MapGazetteerReadiness.Empty, store.GetSnapshot().Readiness);

        store.BeginRequest("session", "mission", "Altis", 30720, "request");
        store.Ingest(Page(0, 2, 2, Location("one", "Agia Marina", "NameCity", 3400, 5600)));
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(MapGazetteerReadiness.Failed, store.GetSnapshot().Readiness);
        Assert.Equal("gazetteer_incomplete_batch", store.GetSnapshot().DiagnosticCode);

        store.Reset();
        Assert.Equal(MapGazetteerReadiness.Unavailable, store.GetSnapshot().Readiness);
        Assert.Empty(store.GetSnapshot().Locations);
    }

    [Fact]
    public void Gazetteer_OversizedPageFailsAndWorldChangeClearsActiveData()
    {
        MapGazetteerStore store = ReadyRequest();
        object[] oversized = Enumerable.Range(0, 129)
            .Select(index => Location($"key-{index}", $"Name {index}", "NameLocal", index, index))
            .ToArray();
        Assert.Equal("gazetteer_page_oversized", store.Ingest(Page(0, 2, 129, oversized)).DiagnosticCode);

        store = ReadyRequest();
        Assert.Equal("gazetteer_totalLocations_invalid", store.Ingest(
            Page(0, 64, 8193, Array.Empty<object>())).DiagnosticCode);

        store = ReadyRequest();
        Assert.Equal("gazetteer_pageCount_invalid", store.Ingest(
            Page(0, 65, 1, Array.Empty<object>())).DiagnosticCode);

        store = ReadyRequest();
        store.Ingest(Page(0, 1, 1, Location("one", "Agia Marina", "NameCity", 3400, 5600)));
        Assert.Single(store.GetSnapshot().Locations);
        store.BeginRequest("session", "new-mission", "Stratis", 8192, "new-request");

        Assert.Equal(MapGazetteerReadiness.Requesting, store.GetSnapshot().Readiness);
        Assert.Empty(store.GetSnapshot().Locations);
        Assert.Equal("Stratis", store.GetSnapshot().WorldName);
    }

    [Fact]
    public void Interpreter_UsesContainmentTypeRankingDistanceBearingAndRoundingDeterministically()
    {
        WorldStateView world = World();
        MapGazetteerSnapshot gazetteer = Snapshot(
            new("inside", "Agia Marina", "NameCity", 3400.1, 5600.2, 200, 100, 45),
            new("hill", "Hill 101", "Hill", 3400.1, 5500.2, 10, 10, 0),
            new("town", "Camp Maxwell", "NameVillage", 3400.1, 5300.2, 100, 100, 0));

        PositionInterpretation result = new PositionInterpretationService().Interpret(world, gazetteer);

        Assert.Equal("Agia Marina", result.PrimaryReference?.Name);
        Assert.True(result.PrimaryReference?.Inside);
        Assert.Equal(0, result.PrimaryReference?.RoundedDistanceMeters);
        InterpretedLocationReference hill = Assert.Single(
            result.AlternativeReferences, item => item.Name == "Hill 101");
        Assert.Equal(100, hill.RoundedDistanceMeters);
        Assert.Equal(0, hill.BearingFromReference);
        Assert.Equal("north", hill.DirectionFromReference);
    }

    [Fact]
    public void Interpreter_PrefersUsefulSettlementOverCloserMinorFeatureAndDeduplicates()
    {
        WorldStateView world = World();
        MapGazetteerSnapshot gazetteer = Snapshot(
            new("hill", "Hill 101", "Hill", 3400.1, 5500.2, 0, 0, 0),
            new("town-a", "Camp Maxwell", "NameVillage", 3400.1, 5300.2, 0, 0, 0),
            new("town-b", "Camp Maxwell", "NameVillage", 3400.1, 5300.2, 0, 0, 0));

        PositionInterpretation result = new PositionInterpretationService().Interpret(world, gazetteer);

        Assert.Equal("Camp Maxwell", result.PrimaryReference?.Name);
        Assert.Equal(2, 1 + result.AlternativeReferences.Count);
    }

    [Fact]
    public void Interpreter_RotatedEllipseBoundaryCountsAsInside()
    {
        PositionInterpretation result = new PositionInterpretationService().Interpret(
            World(), Snapshot(new MapGazetteerLocation(
                "boundary", "Boundary Place", "NameLocal", 3400.1, 5500.2, 100, 50, 90)));

        Assert.True(result.PrimaryReference?.Inside);
    }

    [Fact]
    public void Interpreter_NoValidLocationFallsBackWithoutInventingAName()
    {
        PositionInterpretation result = new PositionInterpretationService().Interpret(
            World(), new MapGazetteerSnapshot(
                MapGazetteerReadiness.Empty, "Altis", 30720,
                Array.Empty<MapGazetteerLocation>(), string.Empty));

        Assert.Null(result.PrimaryReference);
        Assert.Empty(result.AlternativeReferences);
        Assert.Equal("Altis", result.WorldName);
        Assert.Equal("034056", result.Grid);
    }

    [Theory]
    [InlineData(3400.1, 5500.2, "north")]
    [InlineData(3300.1, 5600.2, "east")]
    [InlineData(3400.1, 5700.2, "south")]
    [InlineData(3500.1, 5600.2, "west")]
    [InlineData(3300.1, 5500.2, "northeast")]
    [InlineData(3300.1, 5700.2, "southeast")]
    [InlineData(3500.1, 5700.2, "southwest")]
    [InlineData(3500.1, 5500.2, "northwest")]
    public void Interpreter_CardinalDirectionDescribesPlayerFromReference(double x, double y, string direction)
    {
        PositionInterpretation result = new PositionInterpretationService().Interpret(
            World(), Snapshot(new MapGazetteerLocation("reference", "Reference", "NameLocal", x, y, 0, 0, 0)));

        Assert.Equal(direction, result.PrimaryReference?.DirectionFromReference);
    }

    [Theory]
    [InlineData(94, 90, null)]
    [InlineData(95, 100, null)]
    [InlineData(124, 100, null)]
    [InlineData(125, 150, null)]
    [InlineData(999, 1000, null)]
    [InlineData(1000, 1000, 1.0)]
    [InlineData(1050, 1100, 1.1)]
    public void Interpreter_MilitaryDistanceThresholdsAreExact(
        double distance,
        int rounded,
        double? klicks)
    {
        WorldStateView world = World();
        MapGazetteerSnapshot gazetteer = Snapshot(new MapGazetteerLocation(
            "reference", "Distance Reference", "NameLocal",
            3400.1, 5600.2 - distance, 0, 0, 0));
        using JsonDocument result = JsonDocument.Parse(new PositionInterpretationService().FindNamedLocations(
            world, gazetteer, Arguments("""{"query":null,"maxDistanceMeters":50000,"limit":1}""")));
        JsonElement location = result.RootElement.GetProperty("locations")[0];

        Assert.Equal(rounded, location.GetProperty("roundedDistanceMeters").GetInt32());
        if (klicks is null) Assert.Equal(JsonValueKind.Null, location.GetProperty("distanceKlicks").ValueKind);
        else Assert.Equal(klicks.Value, location.GetProperty("distanceKlicks").GetDouble(), 3);
    }

    [Fact]
    public void Snapshot_IncludesMeasuredPositionAndAtMostThreeReferencesWithoutFullGazetteer()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore worldStore = new(time);
        using TelemetryIngestService ingest = new(worldStore, time);
        ingest.Ingest(WorldModelTestData.Telemetry());
        MapGazetteerStore gazetteerStore = ReadyRequest(time);
        gazetteerStore.Ingest(Page(0, 1, 4,
            Location("SECRET_CONFIG_KEY", "One", "NameCity", 3400, 5600),
            Location("two", "Two", "NameVillage", 3500, 5600),
            Location("three", "Three", "NameLocal", 3600, 5600),
            Location("four", "Never Forwarded", "Hill", 3700, 5600)));
        WorldSnapshotBuilder builder = new(worldStore, time, gazetteerStore);

        string json = builder.BuildCurrentSituation();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement interpretation = document.RootElement.GetProperty("interpretedLocation");

        Assert.Equal(3400.1, interpretation.GetProperty("measuredPosition").GetProperty("x").GetDouble(), 3);
        Assert.Equal(2, interpretation.GetProperty("alternativeReferences").GetArrayLength());
        Assert.DoesNotContain("Never Forwarded", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_CONFIG_KEY", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FindNamedLocations_ValidatesBoundsAndReportsLastKnownPlayerState()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore worldStore = new(time);
        using TelemetryIngestService ingest = new(worldStore, time);
        ingest.Ingest(WorldModelTestData.Telemetry());
        MapGazetteerStore gazetteerStore = ReadyRequest(time);
        gazetteerStore.Ingest(Page(0, 1, 1,
            Location("one", "Agia Marina", "NameCity", 3400, 5600)));
        WorldSnapshotBuilder builder = new(worldStore, time, gazetteerStore);
        time.Advance(TimeSpan.FromSeconds(20));

        using JsonDocument result = JsonDocument.Parse(builder.BuildNamedLocations(Arguments(
            """{"query":"marina","maxDistanceMeters":5000,"limit":5}""")));

        Assert.Equal("last-known", result.RootElement.GetProperty("playerStatus").GetString());
        Assert.Equal("Agia Marina", result.RootElement.GetProperty("locations")[0].GetProperty("name").GetString());
        Assert.Throws<InvalidOperationException>(() => builder.BuildNamedLocations(Arguments(
            """{"query":null,"maxDistanceMeters":999999,"limit":5}""")));
    }

    [Fact]
    public void ContextEnrichment_MeetsTheFiveHundredMillisecondCiGate()
    {
        WorldStateView world = World();
        MapGazetteerSnapshot gazetteer = new(
            MapGazetteerReadiness.Ready, "Altis", 30720,
            Enumerable.Range(0, 8192).Select(index => new MapGazetteerLocation(
                $"key-{index}", $"Location {index}", "NameLocal",
                index % 30720, index * 3 % 30720, 100, 100, 0)).ToArray(), string.Empty);
        PositionInterpretationService interpreter = new();

        Stopwatch stopwatch = Stopwatch.StartNew();
        PositionInterpretation result = interpreter.Interpret(world, gazetteer);
        stopwatch.Stop();

        Assert.NotNull(result.PrimaryReference);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"Elapsed: {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData("Contact east. Over. Over", "over", "Contact east. Over.")]
    [InlineData("Contact east, out!", "out", "Contact east. Out.")]
    [InlineData("Contact east. Over.", "none", "Contact east. Over.")]
    public void TerminatorNormalization_IsExactAndIdempotent(string input, string terminator, string expected)
    {
        ResponseProfileSettings profile = new() { Terminator = terminator };
        string once = ResponseTextNormalizer.Normalize(input, profile);
        string twice = ResponseTextNormalizer.Normalize(once, profile);

        Assert.Equal(expected, once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ResponseProfile_RejectsUnknownModesAndBoundsUntrustedCustomText()
    {
        ResponseProfileSettings result = ResponseProfilePolicy.Normalize(new ResponseProfileSettings
        {
            Preset = "UNSUPPORTED",
            Language = "xx",
            Length = "essay",
            Terminator = "custom",
            CustomTerminator = new string('x', 100),
            CustomStyle = new string('y', 2500) + "\nINJECT"
        });

        Assert.Equal("authentic-military", result.Preset);
        Assert.Equal("auto", result.Language);
        Assert.Equal("short", result.Length);
        Assert.Equal(32, result.CustomTerminator.Length);
        Assert.Equal(2000, result.CustomStyle.Length);
        Assert.DoesNotContain('\n', result.CustomStyle);
        Assert.Contains("STYLE ONLY", ResponseProfilePolicy.BuildPrompt(result), StringComparison.Ordinal);
    }

    [Fact]
    public void ResponseProfile_RoundTripsThroughLocalSettingsJson()
    {
        AppSettings original = new()
        {
            ResponseProfile = new ResponseProfileSettings
            {
                Preset = "custom",
                Language = "de",
                Length = "very-short",
                Terminator = "custom",
                CustomTerminator = "Ende.",
                CustomStyle = "Ruhig und knapp."
            }
        };

        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original))!;

        Assert.Equal("custom", restored.ResponseProfile.Preset);
        Assert.Equal("de", restored.ResponseProfile.Language);
        Assert.Equal("Ruhig und knapp.", restored.ResponseProfile.CustomStyle);
        Assert.Equal("Ende.", restored.ResponseProfile.CustomTerminator);
    }

    private static MapGazetteerStore ReadyRequest(TimeProvider? time = null)
    {
        MapGazetteerStore store = new(time);
        store.BeginRequest("session", "mission", "Altis", 30720, "request");
        return store;
    }

    private static WorldStateView World()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        ingest.Ingest(WorldModelTestData.Telemetry());
        return store.GetCurrentView();
    }

    private static MapGazetteerSnapshot Snapshot(params MapGazetteerLocation[] locations)
        => new(MapGazetteerReadiness.Ready, "Altis", 30720, locations, string.Empty);

    private static object Location(string key, string name, string type, double x, double y)
        => new { key, name, type, position = new[] { x, y }, radiusA = 100.0, radiusB = 100.0, angle = 0.0 };

    private static string Page(
        int pageIndex,
        int pageCount,
        int totalLocations,
        params object[] locations)
        => Page(pageIndex, pageCount, totalLocations, locations, "session");

    private static string Page(
        int pageIndex,
        int pageCount,
        int totalLocations,
        object[] locations,
        string sessionId)
        => JsonSerializer.Serialize(new
        {
            schema = MapGazetteerStore.Schema,
            messageId = $"message-{pageIndex}",
            missionId = "mission",
            sessionId,
            timestamp = 10.0,
            sequence = pageIndex + 2,
            requestId = "request",
            batchId = "batch",
            pageIndex,
            pageCount,
            world = new { name = "Altis", sizeMeters = 30720 },
            totalLocations,
            status = "complete",
            errorCode = (string?)null,
            locations
        });

    private static JsonElement Arguments(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
