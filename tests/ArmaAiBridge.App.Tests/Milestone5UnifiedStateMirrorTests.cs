using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        StateSnapshotMessage parsed = StateSnapshotParser.Parse(fixture.RootElement, Start);
        Assert.Equal(42, parsed.Sequence);
        Assert.True(parsed.FullReconciliation);
        Assert.Equal(8, parsed.Sections.Count);
        Assert.Equal(38, parsed.Sections["environment"].SampledAtGameTime);
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
        legacy.Ingest(handshake); state.Ingest(handshake); state.Ingest(Fixture("state-snapshot-v2.json"));
        WorldSnapshotBuilder builder = new(world, time, stateRepository: repository);
        Assert.True(builder.TryBuildCurrentSituation("Wie steht der Wind?", out string snapshot));
        Assert.Contains("environment", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("net:9:1", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Clear hostile presence", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("marker-objective", snapshot, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(snapshot);
        Assert.Empty(document.RootElement.GetProperty("knownContacts").EnumerateArray());
        Assert.Equal(new[] { "environment" }, document.RootElement.GetProperty("stateMirror").GetProperty("selectedSections").EnumerateArray().Select(item => item.GetString()).ToArray());
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

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arma-ai-state-tests", Guid.NewGuid().ToString("N"));
        public TempDatabase() { Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "state.sqlite3"); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
