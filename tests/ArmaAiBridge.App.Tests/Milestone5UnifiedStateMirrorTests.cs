using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Milestone5UnifiedStateMirrorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StateSnapshotFixture_MatchesClosedVersionedSchemaAndRuntimeParser()
    {
        using JsonDocument schema = JsonDocument.Parse(Fixture("schemas/state-snapshot-v2.schema.json"));
        using JsonDocument fixture = JsonDocument.Parse(Fixture("state-snapshot-v2.json"));
        JsonElement root = schema.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.False(root.GetProperty("properties").GetProperty("sections").GetProperty("additionalProperties").GetBoolean());
        foreach (string section in new[] { "player", "environment", "timeAstronomy", "loadout", "friendlyForces", "knownContacts", "tasks", "markers" })
        {
            string reference = root.GetProperty("properties").GetProperty("sections").GetProperty("properties").GetProperty(section).GetProperty("$ref").GetString()!;
            string definition = reference.Split('/').Last();
            Assert.False(root.GetProperty("$defs").GetProperty(definition).GetProperty("additionalProperties").GetBoolean());
        }
        JsonElement astronomySchema = root.GetProperty("$defs").GetProperty("timeAstronomy");
        string[] readyAstronomyFields = astronomySchema.GetProperty("allOf")[0].GetProperty("then").GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(new[] { "missionDate", "daytime", "elapsedMissionTime", "timeMultiplier", "moonPhase", "sunOrMoon" }, readyAstronomyFields);
        foreach (string removed in new[] { "getLighting", "lightDirection", "starsVisibility", "moonIntensity" })
            Assert.False(astronomySchema.GetProperty("properties").TryGetProperty(removed, out _));
        StateSnapshotMessage parsed = StateSnapshotParser.Parse(fixture.RootElement, Start);
        Assert.Equal(42, parsed.Sequence);
        Assert.True(parsed.FullReconciliation);
        Assert.Equal(8, parsed.Sections.Count);
        Assert.Equal(38, parsed.Sections["environment"].SampledAtGameTime);
        StateTimeAstronomy astronomy = ReadyRepositoryTime(parsed);
        Assert.Equal(0.7, astronomy.MoonPhase);
        Assert.Equal(0.5, astronomy.SunOrMoon);
    }

    [Fact]
    public void Parser_RejectsMissingSectionForbiddenPlayerStateAndHostileActualPosition()
    {
        JsonNode missing = SnapshotNode();
        missing["sections"]!.AsObject().Remove("markers");
        Assert.Equal("state_required_section_missing", ParseFailure(missing));

        JsonNode camera = SnapshotNode();
        camera["sections"]!["player"]!["cameraPosition"] = new JsonArray(1, 2, 3);
        Assert.Equal("state_player_forbidden_field", ParseFailure(camera));

        JsonNode hidden = SnapshotNode();
        hidden["sections"]!["knownContacts"]!["contacts"]![0]!["actualPosition"] = new JsonArray(1, 2, 3);
        Assert.Equal("state_contact_invalid", ParseFailure(hidden));

        JsonNode removedLighting = SnapshotNode();
        removedLighting["sections"]!["timeAstronomy"]!["lightDirection"] = new JsonArray(1, 2, 3);
        Assert.Equal("state_time_unknown_field", ParseFailure(removedLighting));

        JsonNode failedAstronomy = SnapshotNode();
        failedAstronomy["sections"]!["timeAstronomy"] = new JsonObject { ["sampledAt"] = 40, ["readiness"] = "failed" };
        StateSnapshotMessage failedMessage = Parse(failedAstronomy, new ManualTimeProvider(Start));
        Assert.Equal(8, failedMessage.Sections.Count);
        Assert.Equal(StateSectionReadiness.Failed, failedMessage.Sections["timeAstronomy"].Readiness);

        JsonNode missingSpeed = SnapshotNode();
        missingSpeed["sections"]!["friendlyForces"]!["groups"]![0]!.AsObject().Remove("leaderSpeedKph");
        Assert.Equal("state_number_invalid", ParseFailure(missingSpeed));

        JsonNode missingChannel = SnapshotNode();
        missingChannel["sections"]!["markers"]!["markers"]![0]!.AsObject().Remove("channel");
        Assert.Equal("state_integer_invalid", ParseFailure(missingChannel));
    }

    [Fact]
    public void SqliteIngest_IsAtomicBoundedAndStoresOnlySessionAliasesAndHashes()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        Activate(repository);
        StateIngestResult result = repository.ApplySnapshot(ParseSnapshot(time));
        Assert.Equal(TelemetryIngestStatus.Applied, result.Status);

        StateRepositoryDiagnostics diagnostics = repository.GetDiagnostics();
        Assert.Equal(42, diagnostics.LastSequence);
        Assert.Equal(2, diagnostics.Sections.Single(item => item.Section == "environment").AgeSeconds);
        Assert.Equal(1, diagnostics.RowCounts["groups"]);
        Assert.Equal(1, diagnostics.RowCounts["units"]);
        Assert.Equal(1, diagnostics.RowCounts["contacts"]);
        Assert.Equal(1, diagnostics.RowCounts["tasks"]);
        Assert.Equal(1, diagnostics.RowCounts["markers"]);
        Assert.StartsWith("group-", Assert.Single(repository.GetFriendlyGroups()).Alias);
        Assert.StartsWith("contact-", Assert.Single(repository.GetKnownContacts()).Alias);

        repository.Dispose();
        string databaseBytes = Encoding.UTF8.GetString(File.ReadAllBytes(database.Path));
        Assert.DoesNotContain("net:1:1", databaseBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("net:9:1", databaseBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("task-main", databaseBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-objective", databaseBytes, StringComparison.Ordinal);
    }

    [Fact]
    public void BullseyeIdentifier_IsClassifiedLocallyAndRawIdentifierIsDiscarded()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        Activate(repository);
        JsonNode snapshot = SnapshotNode();
        JsonObject marker = snapshot["sections"]!["markers"]!["markers"]![0]!.AsObject();
        marker["sourceId"] = "mission_bullseye_west";
        marker["text"] = "";
        marker["position"] = new JsonArray(1000, 1000, 0);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(snapshot, time)).Status);

        StateMarker stored = Assert.Single(repository.GetMarkers());
        Assert.Equal("bullseye", stored.ReferenceRole);
        Assert.Equal("Bullseye West", stored.ReferenceLabel);
        TacticalPositionDescription report = new TacticalPositionReportingService(repository)
            .Describe(new WorldPosition(1425, 1425, 0));
        Assert.Equal("600 metres northeast of Bullseye West", report.Text);
        Assert.Equal("bullseye", report.ReferenceKind);

        string tacticalSnapshot = JsonSerializer.Serialize(
            new TacticalSnapshotBuilder(repository, repository, time).Build("Where is the enemy contact?"));
        TacticalEvidenceReport contactContext =
            TacticalEvidencePipeline.Build(tacticalSnapshot, "Where is the enemy contact?");
        Assert.Contains("Bullseye West", contactContext.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("mission_bullseye_west", contactContext.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", contactContext.ModelContext, StringComparison.Ordinal);

        string objectiveSnapshot = JsonSerializer.Serialize(
            new TacticalSnapshotBuilder(repository, repository, time).Build("Where is my mission objective?"));
        TacticalEvidenceReport objectiveContext =
            TacticalEvidencePipeline.Build(objectiveSnapshot, "Where is my mission objective?");
        Assert.Contains("Objective Secure the village", objectiveContext.ModelContext, StringComparison.Ordinal);
        Assert.Contains("of Bullseye West", objectiveContext.ModelContext, StringComparison.Ordinal);

        repository.Dispose();
        Assert.DoesNotContain("mission_bullseye_west", Encoding.UTF8.GetString(File.ReadAllBytes(database.Path)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(426, "450 metres")]
    [InlineData(576, "600 metres")]
    [InlineData(1490, "1,500 metres")]
    [InlineData(2780, "3 kilometres")]
    [InlineData(4240, "4 kilometres")]
    public void TacticalPositionDistance_UsesRequiredRounding(double metres, string expected)
        => Assert.Equal(expected, TacticalPositionReportingService.FormatDistance(metres));

    [Fact]
    public void TacticalPositionDirection_IsFromReferenceToTargetAndUsesCardinals()
    {
        WorldPosition origin = new(1000, 1000, 0);
        Assert.Equal("north", TacticalPositionReportingService.Direction(origin, new WorldPosition(1000, 1500, 0)));
        Assert.Equal("northeast", TacticalPositionReportingService.Direction(origin, new WorldPosition(1500, 1500, 0)));
        Assert.Equal("west", TacticalPositionReportingService.Direction(origin, new WorldPosition(500, 1000, 0)));
    }

    [Fact]
    public void TacticalPosition_UsesNamedMissionLocationThenGridFallback()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        Activate(repository);
        JsonNode named = SnapshotNode();
        JsonObject marker = named["sections"]!["markers"]!["markers"]![0]!.AsObject();
        marker["sourceId"] = "checkpoint-alpha";
        marker["text"] = "Checkpoint Alpha";
        marker["position"] = new JsonArray(1000, 1000, 0);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(named, time)).Status);
        TacticalPositionReportingService service = new(repository);
        Assert.Equal("600 metres northeast of Checkpoint Alpha",
            service.Describe(new WorldPosition(1425, 1425, 0)).Text);

        JsonNode empty = SnapshotNode(sequence: 43);
        empty["sections"]!["markers"]!["markers"] = new JsonArray();
        empty["sections"]!["tasks"]!["tasks"]![0]!["active"] = false;
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(empty, time)).Status);
        Assert.Equal("grid 014014", service.Describe(new WorldPosition(1425, 1425, 0)).Text);
    }

    [Fact]
    public void TacticalPosition_UsesOnlyLivingStationaryNonPlayerFriendlyReference()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        Activate(repository);
        JsonNode snapshot = SnapshotNode();
        snapshot["sections"]!["markers"]!["markers"] = new JsonArray();
        snapshot["sections"]!["tasks"]!["tasks"]![0]!["active"] = false;
        JsonArray groups = snapshot["sections"]!["friendlyForces"]!["groups"]!.AsArray();
        JsonObject group = groups[0]!.DeepClone().AsObject();
        group["sourceId"] = "bravo-group";
        group["callsign"] = "Bravo 1-2";
        group["leaderSourceId"] = "bravo-leader";
        group["memberSourceIds"] = new JsonArray("bravo-leader");
        group["leaderPosition"] = new JsonArray(1000, 1000, 0);
        group["leaderSpeedKph"] = 0;
        groups.Add(group);
        JsonArray units = snapshot["sections"]!["friendlyForces"]!["units"]!.AsArray();
        JsonObject unit = units[0]!.DeepClone().AsObject();
        unit["sourceId"] = "bravo-leader";
        unit["groupSourceId"] = "bravo-group";
        unit["position"] = new JsonArray(1000, 1000, 0);
        units.Add(unit);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(snapshot, time)).Status);
        TacticalPositionReportingService service = new(repository);
        Assert.Equal("600 metres northeast of Bravo 1-2",
            service.Describe(new WorldPosition(1425, 1425, 0)).Text);

        JsonNode moving = snapshot.DeepClone();
        moving["sequence"] = 43;
        moving["sections"]!["friendlyForces"]!["groups"]![1]!["leaderSpeedKph"] = 80;
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(moving, time)).Status);
        Assert.Equal("grid 014014", service.Describe(new WorldPosition(1425, 1425, 0)).Text);
    }

    [Fact]
    public void SequenceFailureEmptyAndSessionReset_FollowAuthoritativeSectionRules()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        Activate(repository);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(ParseSnapshot(time)).Status);
        Assert.Equal(TelemetryIngestStatus.OutOfOrder, repository.ApplySnapshot(ParseSnapshot(time)).Status);
        Assert.Single(repository.GetKnownContacts());
        double lastGoodSample = repository.GetSectionMetadata().Single(item => item.Section == "knownContacts").SampledAtGameTime;

        JsonNode failed = SnapshotNode(sequence: 43);
        failed["sections"]!["knownContacts"]!["readiness"] = "failed";
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(failed, time)).Status);
        Assert.Single(repository.GetKnownContacts(includeStale: true));
        StateSectionMetadata failedMetadata = repository.GetSectionMetadata().Single(item => item.Section == "knownContacts");
        Assert.True(failedMetadata.IsStale);
        Assert.Equal(lastGoodSample, failedMetadata.SampledAtGameTime);

        JsonNode empty = SnapshotNode(sequence: 44);
        empty["sections"]!["knownContacts"]!["contacts"] = new JsonArray();
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(empty, time)).Status);
        Assert.Empty(repository.GetKnownContacts(includeStale: true));

        Assert.Equal(TelemetryIngestStatus.Applied,
            repository.ApplyHandshake("other-mission", "other-session", "Stratis", 8192, Start.AddMinutes(1)).Status);
        StateRepositoryDiagnostics reset = repository.GetDiagnostics();
        Assert.Equal(0, reset.LastSequence);
        Assert.Equal(0, reset.RowCounts["groups"]);
        Assert.Equal(0, reset.RowCounts["contacts"]);
    }

    [Fact]
    public void RestartMarksCachedRowsStaleUntilFreshMatchingSessionSnapshot()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using (SqliteStateRepository first = new(database.Path, time))
        {
            Activate(first);
            first.ApplySnapshot(ParseSnapshot(time));
        }
        using SqliteStateRepository restarted = new(database.Path, time);
        Assert.All(restarted.GetSectionMetadata(), item => Assert.True(item.IsStale));
        Assert.Empty(restarted.GetKnownContacts());
        Assert.Single(restarted.GetKnownContacts(includeStale: true));

        Activate(restarted);
        JsonNode next = SnapshotNode(sequence: 43);
        Assert.Equal(TelemetryIngestStatus.Applied, restarted.ApplySnapshot(Parse(next, time)).Status);
        Assert.False(restarted.GetSectionMetadata().Single(item => item.Section == "knownContacts").IsStale);
    }

    [Fact]
    public void FailedAstronomy_PreservesLastGoodTimeAsStaleAndAdvancesSequence()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = ReadyRepository(database.Path, time);
        StateTimeAstronomy good = repository.GetTimeAstronomy()!;

        JsonNode failed = SnapshotNode(sequence: 43);
        failed["sections"]!["timeAstronomy"] = new JsonObject { ["sampledAt"] = 41, ["readiness"] = "failed" };
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(failed, time)).Status);

        StateTimeAstronomy stale = repository.GetTimeAstronomy()!;
        Assert.Equal(good.MoonPhase, stale.MoonPhase);
        Assert.Equal(good.SunOrMoon, stale.SunOrMoon);
        Assert.True(stale.Metadata.IsStale);
        Assert.Equal(StateSectionReadiness.Failed, stale.Metadata.Readiness);
        Assert.Equal(43, repository.GetDiagnostics().LastSequence);
    }

    [Fact]
    public void SchemaVersionOne_IsMigratedWithoutDiscardingCachedSectionMetadata()
    {
        using TempDatabase database = new();
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE state_section_metadata(
                    session_hash TEXT NOT NULL, section TEXT NOT NULL, readiness TEXT NOT NULL,
                    sampled_at REAL NOT NULL, received_utc TEXT NOT NULL, is_stale INTEGER NOT NULL,
                    PRIMARY KEY(session_hash,section));
                INSERT INTO state_section_metadata VALUES('old-session','environment','ready',10,'2026-07-22T12:00:00.0000000+00:00',0);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }

        using SqliteStateRepository repository = new(database.Path, new ManualTimeProvider(Start));
        Assert.Equal(StateRepositoryReadiness.Ready, repository.GetDiagnostics().Readiness);
        Assert.Equal(5, repository.GetDiagnostics().SchemaVersion);
    }

    [Fact]
    public void SchemaVersionTwo_PurgesLegacyContactsBeforeTheyCanBeRead()
    {
        using TempDatabase database = new();
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE known_contacts(session_hash TEXT NOT NULL, alias TEXT PRIMARY KEY,
                    source_hash TEXT NOT NULL, payload_json TEXT NOT NULL);
                CREATE TABLE known_contact_sources(contact_alias TEXT NOT NULL, group_alias TEXT NOT NULL,
                    PRIMARY KEY(contact_alias,group_alias));
                INSERT INTO known_contacts VALUES('old','contact-deadbeef','hash','{"contactType":"other","class":"Land_LampHalogen_F"}');
                INSERT INTO known_contact_sources VALUES('contact-deadbeef','group-deadbeef');
                PRAGMA user_version=2;
                """;
            command.ExecuteNonQuery();
        }

        using (SqliteStateRepository repository = new(database.Path, new ManualTimeProvider(Start)))
            Assert.Equal(5, repository.GetDiagnostics().SchemaVersion);

        using SqliteConnection verify = new($"Data Source={database.Path};Pooling=False");
        verify.Open();
        using SqliteCommand count = verify.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM known_contacts) + (SELECT COUNT(*) FROM known_contact_sources);";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
    }

    [Fact]
    public void DeterministicInterpreters_DeriveOvercastLoadoutForceAndContactFacts()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = ReadyRepository(database.Path, time);
        EnvironmentInterpretation environment = new EnvironmentInterpretationService().Interpret(
            repository.GetEnvironment()!, repository.GetTimeAstronomy());
        Assert.Equal(0.4, environment.Overcast);
        Assert.Equal("unsettled", environment.Condition);

        LoadoutSummary loadout = new LoadoutSummaryService().Summarize(repository.GetLoadout()!);
        Assert.Equal(27, loadout.LoadedRounds);
        Assert.Equal(1, loadout.ReserveMagazines);
        Assert.Equal(30, loadout.ReserveRounds);
        Assert.Equal(2, loadout.Grenades);

        ForceSummary force = new ForceSummaryService().Summarize(repository.GetFriendlyGroups(), repository.GetFriendlyUnits());
        Assert.Equal(1, force.GroupCount); Assert.Equal(1, force.UnitCount); Assert.Equal(0, force.DeadCount);
        ContactSummary contacts = new ContactSummaryService().Summarize(repository.GetKnownContacts());
        Assert.Equal(1, contacts.KnownContactCount); Assert.Equal(1, contacts.ByRelationship["hostile"]);
        Assert.Equal(25, contacts.MaximumPositionUncertaintyMeters);
    }

    [Fact]
    public void QueryState_IsTypedBoundedAndRejectsArbitrarySectionsOrArguments()
    {
        using TempDatabase database = new();
        using SqliteStateRepository repository = ReadyRepository(database.Path, new ManualTimeProvider(Start));
        StateQueryService service = new(repository);
        string contacts = service.Query(Arguments("""{"section":"contacts","includeStale":false,"limit":1}"""));
        Assert.Contains("hostile infantry", contacts, StringComparison.Ordinal);
        Assert.DoesNotContain("net:9:1", contacts, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => service.Query(Arguments("""{"section":"sql","includeStale":false,"limit":1}""")));
        Assert.Throws<InvalidOperationException>(() => service.Query(Arguments("""{"section":"contacts","includeStale":false,"limit":1,"sql":"SELECT *"}""")));
    }

    [Fact]
    public void OperationalTurn_UsesOneFixedCompactSnapshotAcrossAllDomains()
    {
        string json = CanonicalSnapshot("Which friendly group is closest to the newest contact?");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(OperationalSnapshotBuilder.Schema, root.GetProperty("schema").GetString());
        foreach (string domain in new[]
        {
            "player", "environment", "time", "friendlyForces", "enemyContacts", "objectives", "markers", "retrievedMemory", "lore"
        })
            Assert.True(root.TryGetProperty(domain, out _), domain);
        Assert.Equal("Alpha 1-1", root.GetProperty("player").GetProperty("groupCallsign").GetString());
        foreach (string removed in new[] { "world", "namedLocations", "loadout", "tasks", "capabilities", "knownContacts" })
            Assert.False(root.TryGetProperty(removed, out _), removed);
        Assert.DoesNotContain("sourceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Alias", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("readiness", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ageSeconds", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= TacticalSnapshotBuilder.MaximumPayloadBytes, $"Tactical context bytes: {Encoding.UTF8.GetByteCount(json)}");
    }

    [Fact]
    public void OperationalSnapshot_PreservesMeaningfulZerosAndEnforcesInitialLimits()
    {
        string json = CanonicalSnapshot("What is my current situation?", node =>
        {
            node["sections"]!["environment"]!["rain"] = 0;
            node["sections"]!["environment"]!["fog"] = 0;
            JsonArray groups = node["sections"]!["friendlyForces"]!["groups"]!.AsArray();
            JsonArray contacts = node["sections"]!["knownContacts"]!["contacts"]!.AsArray();
            JsonArray markers = node["sections"]!["markers"]!["markers"]!.AsArray();
            JsonNode groupTemplate = groups[0]!.DeepClone();
            JsonNode contactTemplate = contacts[0]!.DeepClone();
            JsonNode markerTemplate = markers[0]!.DeepClone();
            for (int index = 1; index < 12; index++)
            {
                JsonNode group = groupTemplate.DeepClone(); group["sourceId"] = $"group-{index}"; group["callsign"] = $"Group {index}"; groups.Add(group);
                JsonNode contact = contactTemplate.DeepClone(); contact["sourceId"] = $"contact-{index}"; contacts.Add(contact);
                JsonNode marker = markerTemplate.DeepClone(); marker["sourceId"] = $"marker-{index}"; marker["text"] = $"Marker {index}"; markers.Add(marker);
            }
        });
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement environment = root.GetProperty("environment");
        Assert.Equal(0.4, environment.GetProperty("overcast").GetDouble());
        Assert.Equal("unsettled", environment.GetProperty("condition").GetString());
        foreach (string removed in new[] { "rain", "fog", "wind", "temperatureCelsius" })
            Assert.False(environment.TryGetProperty(removed, out _), removed);
        Assert.False(root.TryGetProperty("loadout", out _));
        Assert.Equal(12, root.GetProperty("friendlyForces").GetProperty("groups").GetArrayLength());
        Assert.Equal(12, root.GetProperty("enemyContacts").GetProperty("records").GetArrayLength());
        Assert.Equal(12, root.GetProperty("markers").GetProperty("records").GetArrayLength());
    }

    [Fact]
    public void NonOperationalQuestion_StillReceivesBoundedMemoryAndLoreSnapshot()
        => Assert.Equal(OperationalSnapshotBuilder.Schema,
            JsonDocument.Parse(CanonicalSnapshot("How do I bake a loaf of bread?")).RootElement.GetProperty("schema").GetString());

    [Fact]
    public void EmptyArmaCallsign_IsOmittedFromCompactPlayerContext()
    {
        string json = CanonicalSnapshot("What is my position?", node =>
            node["sections"]!["player"]!["groupCallsign"] = string.Empty);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.GetProperty("player").TryGetProperty("groupCallsign", out _));
    }

    [Fact]
    public void NoQuestionDiagnosticsPreview_ShowsTheFixedCompactEligibleShape()
    {
        using JsonDocument document = JsonDocument.Parse(CanonicalSnapshot(string.Empty));
        Assert.Equal(OperationalSnapshotBuilder.Schema, document.RootElement.GetProperty("schema").GetString());
        Assert.True(document.RootElement.TryGetProperty("player", out _));
        Assert.True(document.RootElement.TryGetProperty("retrievedMemory", out _));
        Assert.False(document.RootElement.TryGetProperty("capabilities", out _));
        Assert.False(document.RootElement.TryGetProperty("stateMirror", out _));
        Assert.False(document.RootElement.TryGetProperty("reconciliation", out _));
    }

    [Fact]
    public void FailedOptionalAstronomySnapshot_StillAdvancesAndProjectsPlayerTelemetry()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        WorldStateStore world = new(time);
        using TelemetryIngestService legacy = new(world, time);
        using StateMirrorIngestService state = new(repository, legacy, timeProvider: time);
        string handshake = Handshake();
        legacy.Ingest(handshake); state.Ingest(handshake);
        JsonNode snapshot = SnapshotNode();
        snapshot["sections"]!["timeAstronomy"] = new JsonObject { ["sampledAt"] = 40, ["readiness"] = "failed" };

        Assert.Equal(TelemetryIngestStatus.Applied, state.Ingest(snapshot.ToJsonString()).Status);
        Assert.Equal(42, repository.GetDiagnostics().LastSequence);
        Assert.True(world.GetCurrentView().HasTelemetry);
        Assert.Equal(StateSectionReadiness.Failed,
            repository.GetSectionMetadata().Single(item => item.Section == "timeAstronomy").Readiness);
        Assert.True(new WorldSnapshotBuilder(world, time, stateRepository: repository)
            .TryBuildCurrentSituation("Wo bin ich?", out _));
    }

    [Fact]
    public void SqfContract_UsesCachesLocalKnowledgeAndNoLegacyOrHiddenPositionLoop()
    {
        using JsonDocument contract = JsonDocument.Parse(Fixture("sqf-milestone-5-state-mirror-v2.json"));
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        foreach (JsonElement file in contract.RootElement.GetProperty("files").EnumerateArray())
        {
            string source = File.ReadAllText(Path.Combine(root, file.GetProperty("path").GetString()!));
            foreach (JsonElement token in file.GetProperty("requiredTokens").EnumerateArray())
                Assert.Contains(token.GetString()!, source, StringComparison.Ordinal);
            foreach (JsonElement token in file.GetProperty("forbiddenTokens").EnumerateArray())
                Assert.DoesNotContain(token.GetString()!, source, StringComparison.Ordinal);
        }

        string publisher = File.ReadAllText(Path.Combine(root, "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishStateSnapshot.sqf"));
        Assert.Empty(Regex.Matches(publisher, @"\bgetLighting\b", RegexOptions.CultureInvariant).Cast<Match>());
        foreach ((string section, int cadence) in new[]
        {
            ("environment", 8), ("timeAstronomy", 8), ("loadout", 4),
            ("friendlyForces", 2), ("knownContacts", 2), ("tasks", 4), ("markers", 4)
        })
            Assert.Matches($@"\[\s*""{section}""\s*,\s*{cadence}\s*,\s*\{{", publisher);
        Assert.True(publisher.IndexOf("call _runSection", StringComparison.Ordinal) < publisher.IndexOf("private _lastPublish", StringComparison.Ordinal));
    }

    [Fact]
    public void DeprecatedLightingFields_AreAbsentFromRuntimeSchemaAndPayloadFixtures()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        foreach (string relative in new[]
        {
            "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishStateSnapshot.sqf",
            "schemas/state-snapshot-v2.schema.json",
            "tests/fixtures/state-snapshot-v2.json",
            "src/ArmaAiBridge.App/Models/StateMirrorModels.cs",
            "src/ArmaAiBridge.App/Services/StateSnapshotParser.cs",
            "src/ArmaAiBridge.App/Services/SqliteStateRepository.cs"
        })
        {
            string source = File.ReadAllText(Path.Combine(root, relative));
            foreach (string removed in new[] { "sunDirection", "getLighting", "lightDirection", "starsVisibility", "moonIntensity" })
                Assert.DoesNotContain(removed, source, StringComparison.Ordinal);
        }
    }

    private static SqliteStateRepository ReadyRepository(string path, ManualTimeProvider time)
    {
        SqliteStateRepository repository = new(path, time); Activate(repository); repository.ApplySnapshot(ParseSnapshot(time)); return repository;
    }
    private static void Activate(SqliteStateRepository repository)
        => Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplyHandshake("stratis/state-mirror-acceptance", "session-fixture-1", "Stratis", 8192, Start).Status);
    private static StateSnapshotMessage ParseSnapshot(ManualTimeProvider time)
    {
        using JsonDocument document = JsonDocument.Parse(Fixture("state-snapshot-v2.json"));
        return StateSnapshotParser.Parse(document.RootElement, time.GetUtcNow());
    }
    private static StateTimeAstronomy ReadyRepositoryTime(StateSnapshotMessage parsed)
    {
        JsonElement value = parsed.Sections["timeAstronomy"].Payload;
        return new StateTimeAstronomy(
            value.GetProperty("missionDate").EnumerateArray().Select(item => item.GetInt32()).ToArray(),
            value.GetProperty("daytime").GetDouble(), value.GetProperty("elapsedMissionTime").GetDouble(),
            value.GetProperty("timeMultiplier").GetDouble(), value.GetProperty("moonPhase").GetDouble(),
            value.GetProperty("sunOrMoon").GetDouble(),
            new StateSectionMetadata("timeAstronomy", StateSectionReadiness.Ready, 38, Start, 2, false));
    }
    private static StateSnapshotMessage Parse(JsonNode node, ManualTimeProvider time)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        return StateSnapshotParser.Parse(document.RootElement, time.GetUtcNow());
    }
    private static string ParseFailure(JsonNode node)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        return Assert.Throws<InvalidDataException>(() => StateSnapshotParser.Parse(document.RootElement, Start)).Message;
    }
    private static JsonNode SnapshotNode(long sequence = 42)
    {
        JsonNode node = JsonNode.Parse(Fixture("state-snapshot-v2.json"))!;
        node["sequence"] = sequence; node["messageId"] = $"message-{sequence}"; node["timestamp"] = 40 + sequence - 42;
        return node;
    }
    private static string Handshake() => JsonSerializer.Serialize(new
    {
        schema = "arma-ai-bridge/arma3/session-handshake-v1", messageId = "message-1",
        missionId = "stratis/state-mirror-acceptance", sessionId = "session-fixture-1", timestamp = 1, sequence = 1,
        protocol = new { major = 1, minor = 0 }, world = new { name = "Stratis", sizeMeters = 8192 },
        viewer = new { side = "WEST", visibility = "own-side" },
        features = new[] { new { name = "state-snapshot", version = 2 }, new { name = "map-gazetteer", version = 1 } }
    });
    private static JsonElement Arguments(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static string CanonicalSnapshot(string question, Action<JsonNode>? mutate = null)
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        WorldStateStore world = new(time);
        using TelemetryIngestService legacy = new(world, time);
        using StateMirrorIngestService mirror = new(repository, legacy, timeProvider: time);
        string handshake = Handshake();
        legacy.Ingest(handshake);
        mirror.Ingest(handshake);
        JsonNode snapshot = SnapshotNode();
        mutate?.Invoke(snapshot);
        Assert.Equal(TelemetryIngestStatus.Applied, mirror.Ingest(snapshot.ToJsonString()).Status);
        WorldSnapshotBuilder builder = new(world, time, stateRepository: repository);
        string result;
        bool built = question.Length == 0
            ? builder.TryBuildCurrentSituation(out result)
            : builder.TryBuildCurrentSituation(question, out result);
        Assert.True(built);
        return result;
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arma-ai-state-tests", Guid.NewGuid().ToString("N"));
        public TempDatabase() { Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "state.sqlite3"); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
