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
        Assert.Equal(2, repository.GetDiagnostics().SchemaVersion);
    }

    [Fact]
    public void DeterministicInterpreters_DeriveWeatherLoadoutForceAndContactFacts()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = ReadyRepository(database.Path, time);
        EnvironmentInterpretation environment = new EnvironmentInterpretationService().Interpret(
            repository.GetEnvironment()!, repository.GetTimeAstronomy());
        Assert.Equal(5, environment.WindSpeedMetersPerSecond);
        Assert.Equal(37, environment.WindBearingDegrees);
        Assert.Equal("northeast", environment.WindCardinalDirection);
        Assert.Equal("light", environment.RainClassification);

        LoadoutSummary loadout = new LoadoutSummaryService().Summarize(repository.GetLoadout()!);
        Assert.Equal(27, loadout.LoadedRounds);
        Assert.Equal(1, loadout.ReserveMagazines);
        Assert.Equal(30, loadout.ReserveRounds);
        Assert.Equal(2, loadout.Grenades);

        ForceSummary force = new ForceSummaryService().Summarize(repository.GetFriendlyGroups(), repository.GetFriendlyUnits());
        Assert.Equal(1, force.GroupCount); Assert.Equal(1, force.UnitCount); Assert.Equal(0, force.DeadCount);
        ContactSummary contacts = new ContactSummaryService().Summarize(repository.GetKnownContacts());
        Assert.Equal(1, contacts.KnownContactCount); Assert.Equal(1, contacts.ByPerceivedSide["EAST"]);
        Assert.Equal(25, contacts.MaximumPositionUncertaintyMeters);
    }

    [Theory]
    [InlineData("Wo bin ich?", "position")]
    [InlineData("Wie steht der Wind?", "environment")]
    [InlineData("Ist es schon dunkel?", "time")]
    [InlineData("Wie viel Munition habe ich?", "loadout")]
    [InlineData("Welche eigenen Gruppen sind da?", "friendly_forces")]
    [InlineData("Welche Feindkontakte sind bekannt?", "contacts")]
    [InlineData("Was ist unser Auftrag?", "tasks")]
    [InlineData("Show me the map markers", "markers")]
    public void ContextSelector_RecognizesGermanAndEnglishWithoutAModelCall(string question, string expected)
    {
        using TempDatabase database = new();
        using SqliteStateRepository repository = ReadyRepository(database.Path, new ManualTimeProvider(Start));
        StateContextSelection selection = new StateContextSelector(repository).Select(question);
        Assert.Contains(expected, selection.SelectedSections);
    }

    [Fact]
    public void QueryState_IsTypedBoundedAndRejectsArbitrarySectionsOrArguments()
    {
        using TempDatabase database = new();
        using SqliteStateRepository repository = ReadyRepository(database.Path, new ManualTimeProvider(Start));
        StateQueryService service = new(repository);
        string contacts = service.Query(Arguments("""{"section":"contacts","includeStale":false,"limit":1}"""));
        Assert.Contains("contact-", contacts, StringComparison.Ordinal);
        Assert.DoesNotContain("net:9:1", contacts, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => service.Query(Arguments("""{"section":"sql","includeStale":false,"limit":1}""")));
        Assert.Throws<InvalidOperationException>(() => service.Query(Arguments("""{"section":"contacts","includeStale":false,"limit":1,"sql":"SELECT *"}""")));
    }

    [Fact]
    public void InitialOpenAiContext_IsQuestionSelectedAndNeverContainsCompleteStateOrRawIds()
    {
        using TempDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        WorldStateStore world = new(time);
        using TelemetryIngestService legacy = new(world, time);
        using StateMirrorIngestService state = new(repository, legacy, timeProvider: time);
        string handshake = Handshake();
        legacy.Ingest(handshake); state.Ingest(handshake);
        Assert.Equal(TelemetryIngestStatus.Applied, state.Ingest(Fixture("state-snapshot-v2.json")).Status);
        Assert.Equal(42, repository.GetDiagnostics().LastSequence);
        Assert.True(world.GetCurrentView().HasTelemetry);
        WorldSnapshotBuilder builder = new(world, time, stateRepository: repository);
        Assert.True(builder.TryBuildCurrentSituation("Wie steht der Wind?", out string snapshot));
        Assert.Contains("environment", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("net:9:1", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear hostile presence", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-objective", snapshot, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(snapshot);
        Assert.False(document.RootElement.TryGetProperty("knownContacts", out _));
        Assert.False(document.RootElement.TryGetProperty("player", out _));
        Assert.False(document.RootElement.TryGetProperty("reconciliation", out _));
        Assert.Equal(new[] { "environment" }, document.RootElement.GetProperty("stateMirror").GetProperty("selectedSections").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public void CanonicalV2PositionContext_HasNoLegacyProjectionOrDuplicateMeasuredPosition()
    {
        using JsonDocument document = JsonDocument.Parse(CanonicalSnapshot("Wo bin ich?"));
        JsonElement root = document.RootElement;

        Assert.Equal("current-situation", root.GetProperty("purpose").GetString());
        Assert.True(root.TryGetProperty("interpretedLocation", out JsonElement location));
        Assert.Equal("Stratis", location.GetProperty("worldName").GetString());
        Assert.Equal("020056", location.GetProperty("grid").GetString());
        Assert.False(location.TryGetProperty("measuredPosition", out _));
        Assert.Equal(new[] { "position" }, SelectedSections(root));
        Assert.Empty(root.GetProperty("stateMirror").GetProperty("selectedContext").EnumerateObject());

        string json = root.GetRawText();
        foreach (string forbidden in new[]
        {
            "bodyHeading", "viewHeading", "speedKph", "damage", "lifeState", "stance",
            "matchingMagazineCount", "matchingMagazineRounds", "knownContacts", "vehicle",
            "reconciliation", "friendlyForceSummary", "missionCapabilitySummary"
        })
            Assert.DoesNotContain($"\"{forbidden}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactCoordinateQuestion_AddsCanonicalMeasuredPositionOnlyOnce()
    {
        string json = CanonicalSnapshot("What are my exact coordinates?");
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("interpretedLocation").TryGetProperty("measuredPosition", out _));
        Assert.Single(Regex.Matches(json, "\"measuredPosition\"", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.DoesNotContain("\"player\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalV2Context_IsStrictlyQuestionSpecificAndPreservesMeaningfulZeros()
    {
        using JsonDocument weather = JsonDocument.Parse(CanonicalSnapshot("Wie ist das Wetter und aus welcher Richtung kommt der Wind?", node =>
        {
            node["sections"]!["environment"]!["rain"] = 0;
            node["sections"]!["environment"]!["fog"] = 0;
        }));
        JsonElement weatherContext = weather.RootElement.GetProperty("stateMirror").GetProperty("selectedContext");
        Assert.Equal(0, weatherContext.GetProperty("environment").GetProperty("rain").GetDouble());
        Assert.Equal(0, weatherContext.GetProperty("environment").GetProperty("fog").GetDouble());
        Assert.False(weatherContext.TryGetProperty("loadout", out _));
        Assert.False(weatherContext.TryGetProperty("contacts", out _));
        Assert.False(weatherContext.TryGetProperty("tasks", out _));
        Assert.False(weatherContext.TryGetProperty("markers", out _));
        Assert.False(weather.RootElement.TryGetProperty("interpretedLocation", out _));

        using JsonDocument ammunition = JsonDocument.Parse(CanonicalSnapshot("Wie viel Munition habe ich?"));
        JsonElement ammunitionContext = ammunition.RootElement.GetProperty("stateMirror").GetProperty("selectedContext");
        Assert.Equal(new[] { "loadout" }, SelectedSections(ammunition.RootElement));
        Assert.Equal(27, ammunitionContext.GetProperty("loadout").GetProperty("loadedRounds").GetInt32());
        Assert.Equal(0, ammunitionContext.GetProperty("loadout").GetProperty("mines").GetInt32());
        Assert.False(ammunitionContext.TryGetProperty("environment", out _));
        Assert.False(ammunitionContext.TryGetProperty("contacts", out _));
        Assert.False(ammunition.RootElement.TryGetProperty("interpretedLocation", out _));
    }

    [Fact]
    public void ContactContext_IsBoundedAndAuthoritativeZeroIsNotOmitted()
    {
        using JsonDocument current = JsonDocument.Parse(CanonicalSnapshot("Welche Feindkontakte sind bekannt?"));
        JsonElement contacts = current.RootElement.GetProperty("stateMirror").GetProperty("selectedContext").GetProperty("contacts");
        Assert.Equal(1, contacts.GetProperty("summary").GetProperty("knownContactCount").GetInt32());
        Assert.Single(contacts.GetProperty("contacts").EnumerateArray());
        Assert.False(current.RootElement.TryGetProperty("interpretedLocation", out _));

        using JsonDocument empty = JsonDocument.Parse(CanonicalSnapshot("Welche Feindkontakte sind bekannt?", node =>
            node["sections"]!["knownContacts"]!["contacts"] = new JsonArray()));
        JsonElement emptyContacts = empty.RootElement.GetProperty("stateMirror").GetProperty("selectedContext").GetProperty("contacts");
        Assert.Equal(0, emptyContacts.GetProperty("summary").GetProperty("knownContactCount").GetInt32());
        Assert.False(emptyContacts.TryGetProperty("contacts", out _));
    }

    [Fact]
    public void OrdinaryConversation_ReceivesNoArmaStateAndBroadSituationIsCompact()
    {
        string ordinary = CanonicalSnapshot("How do I bake a loaf of bread?");
        using JsonDocument ordinaryDocument = JsonDocument.Parse(ordinary);
        Assert.Equal("current-situation", ordinaryDocument.RootElement.GetProperty("purpose").GetString());
        Assert.False(ordinaryDocument.RootElement.TryGetProperty("stateMirror", out _));
        Assert.False(ordinaryDocument.RootElement.TryGetProperty("interpretedLocation", out _));
        Assert.True(Encoding.UTF8.GetByteCount(ordinary) <= 160, $"Ordinary context bytes: {Encoding.UTF8.GetByteCount(ordinary)}");

        string broad = CanonicalSnapshot("What is the current situation?");
        using JsonDocument broadDocument = JsonDocument.Parse(broad);
        string[] selected = SelectedSections(broadDocument.RootElement);
        Assert.Equal(new[] { "position", "environment", "friendly_forces", "contacts", "tasks" }, selected);
        Assert.True(Encoding.UTF8.GetByteCount(broad) <= 5000, $"Broad context bytes: {Encoding.UTF8.GetByteCount(broad)}");
        Assert.DoesNotContain("marker-objective", broad, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactContext_OmitsEmptyPlaceholdersRemovedAstronomyAndCompleteSnapshot()
    {
        string loadout = CanonicalSnapshot("What ammunition and equipment do I have?", node =>
        {
            node["sections"]!["loadout"]!["selectedWeapon"] = "";
            node["sections"]!["loadout"]!["selectedWeaponDisplayName"] = "";
        });
        using JsonDocument document = JsonDocument.Parse(loadout);
        JsonElement selected = document.RootElement.GetProperty("stateMirror").GetProperty("selectedContext").GetProperty("loadout");
        Assert.False(selected.TryGetProperty("currentWeapon", out _));
        Assert.False(selected.TryGetProperty("currentWeaponDisplayName", out _));
        foreach (string removed in new[] { "getLighting", "lightDirection", "starsVisibility", "moonIntensity" })
            Assert.DoesNotContain(removed, loadout, StringComparison.Ordinal);
        Assert.DoesNotContain("state-snapshot-v2", loadout, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(loadout) <= 1600, $"Loadout context bytes: {Encoding.UTF8.GetByteCount(loadout)}");

        using JsonDocument emptyTasks = JsonDocument.Parse(CanonicalSnapshot("What is our mission objective?", node =>
            node["sections"]!["tasks"]!["tasks"] = new JsonArray()));
        Assert.False(emptyTasks.RootElement.GetProperty("stateMirror").GetProperty("selectedContext")
            .TryGetProperty("tasks", out _));
        using JsonDocument emptyMarkers = JsonDocument.Parse(CanonicalSnapshot("Show me the map markers", node =>
            node["sections"]!["markers"]!["markers"] = new JsonArray()));
        Assert.False(emptyMarkers.RootElement.GetProperty("stateMirror").GetProperty("selectedContext")
            .TryGetProperty("markers", out _));
    }

    [Fact]
    public void NoQuestionDiagnosticsPreview_IsExplicitAndNeverShowsLegacyUnion()
    {
        using JsonDocument document = JsonDocument.Parse(CanonicalSnapshot(string.Empty));
        JsonElement root = document.RootElement;
        Assert.Equal("current-situation-preview", root.GetProperty("purpose").GetString());
        Assert.Empty(SelectedSections(root));
        Assert.Empty(root.GetProperty("stateMirror").GetProperty("selectedContext").EnumerateObject());
        Assert.False(root.TryGetProperty("player", out _));
        Assert.False(root.TryGetProperty("environment", out _));
        Assert.False(root.TryGetProperty("reconciliation", out _));
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

    private static string[] SelectedSections(JsonElement root)
        => root.GetProperty("stateMirror").GetProperty("selectedSections").EnumerateArray()
            .Select(item => item.GetString()!).ToArray();

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arma-ai-state-tests", Guid.NewGuid().ToString("N"));
        public TempDatabase() { Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "state.sqlite3"); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
