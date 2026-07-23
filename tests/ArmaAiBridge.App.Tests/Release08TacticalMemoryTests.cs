using System.Text;
using System.Text.Json;
using System.Net;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Release08TacticalMemoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TacticalSnapshot_RemovesBroadStateAndIncludesEveryBoundedRecord()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, groups: 12, contacts: 12, markers: 12);
        string json = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("Situation report"));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(TacticalSnapshotBuilder.Schema, root.GetProperty("schema").GetString());
        foreach (string removed in new[] { "world", "namedLocations", "loadout", "tasks", "capabilities", "knownContacts" })
            Assert.False(root.TryGetProperty(removed, out _), removed);
        JsonElement player = root.GetProperty("player");
        Assert.Equal(2, player.EnumerateObject().Count());
        Assert.False(player.TryGetProperty("position", out _));
        Assert.Equal(12, root.GetProperty("friendlyForces").GetProperty("groups").GetArrayLength());
        Assert.Equal(12, root.GetProperty("enemyContacts").GetProperty("records").GetArrayLength());
        Assert.Equal(12, root.GetProperty("markers").GetProperty("records").GetArrayLength());
        Assert.True(Encoding.UTF8.GetByteCount(json) <= TacticalSnapshotBuilder.MaximumPayloadBytes);
    }

    [Fact]
    public void FriendlyProjection_IsApproximateHighLevelAndContainsNoOrdersOrRawIdentity()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start), groups: 1);
        JsonElement group = JsonDocument.Parse(JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository).Build("friendlies nearby")))
            .RootElement.GetProperty("friendlyForces").GetProperty("groups")[0].Clone();
        Assert.Equal(10, group.GetProperty("approximatePosition").GetProperty("precisionMeters").GetInt32());
        Assert.Contains("infantry", group.GetProperty("elementType").GetString(), StringComparison.Ordinal);
        foreach (string removed in new[] { "leaderPosition", "leaderAlias", "behaviour", "combatMode", "formation", "waypoint", "expectedDestination", "assignedTargets", "alias" })
            Assert.False(group.TryGetProperty(removed, out _), removed);
    }

    [Fact]
    public void PlayerOwnGroup_NeverProjectsPositionRangeOrBearing()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        Dictionary<string, StateSnapshotSection> sections = new()
        {
            ["player"] = Section("player", Json("""{"sourceId":"player","side":"WEST","groupSourceId":"self-group","groupCallsign":"Alpha 1-1","positionATL":[6830,9990,0],"positionASL":[6830,9990,25],"grid":"068099"}""")),
            ["friendlyForces"] = Section("friendlyForces", Json("""
            {"groups":[{"sourceId":"self-group","callsign":"Alpha 1-1","leaderSourceId":"player","memberSourceIds":["player"],"leaderPosition":[6830,9990,0],"leaderSpeedKph":0,"behaviour":"AWARE","combatMode":"YELLOW","formation":"WEDGE","assignedTargetSourceIds":[]}],"units":[{"sourceId":"player","groupSourceId":"self-group","class":"B_Soldier_F","displayRole":"rifleman","position":[6830,9990,0],"alive":true,"lifeState":"HEALTHY","mobile":true,"damage":0,"currentCommand":"MOVE","assignedTargetSourceId":"","vehicleSourceId":"","vehicleRole":""}]}
            """))
        };
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Snapshot(2, sections)).Status);

        string json = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("Where am I?"));
        JsonElement group = JsonDocument.Parse(json).RootElement.GetProperty("friendlyForces").GetProperty("groups")[0].Clone();
        Assert.Equal("Alpha 1-1", group.GetProperty("callsign").GetString());
        Assert.False(group.TryGetProperty("approximatePosition", out _));
        Assert.False(group.TryGetProperty("rangeFromPlayerMeters", out _));
        Assert.False(group.TryGetProperty("bearingFromPlayerDegrees", out _));
        Assert.DoesNotContain("6830", json, StringComparison.Ordinal);
        Assert.DoesNotContain("9990", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpreter_UsesHumanSourceSectionsAndWithholdsRawSpatialObjects()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, groups: 1, contacts: 1, markers: 1);
        repository.Remember("A strange red tower was reported at bearing 310, about 300 metres away.", "user-reported", ["tower"]);
        repository.SaveLoreSection("Mission", "Red structures may be radio relay towers.", enabled: true, alwaysInclude: true);
        string json = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("What do we know about the red tower?"));

        TacticalEvidenceReport report = TacticalContextInterpreter.Analyze(json, "What do we know about the red tower?");

        Assert.Contains("category=player", report.CandidateEvidence, StringComparison.Ordinal);
        Assert.Contains("category=hostile-contact", report.CandidateEvidence, StringComparison.Ordinal);
        Assert.Contains("provenance=user-reported", report.SelectedEvidence, StringComparison.Ordinal);
        Assert.Contains("user-authored-lore", report.SelectedEvidence, StringComparison.Ordinal);
        Assert.Contains("strange red tower", report.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("estimatedPosition", report.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("approximatePosition", report.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("contactTrackReference", report.CandidateEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", report.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("\"y\"", report.ModelContext, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceFusion_PlayerEnemyReportOutranksEmptyCanonicalContactSet()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, contacts: 0);
        const string playerReport = "It seems there is enemy movement at the radio dish in the south corner.";
        repository.Remember(playerReport, "user-reported", ["enemy", "radio-dish"]);
        string snapshot = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build(playerReport));

        TacticalEvidenceReport report = TacticalContextInterpreter.Analyze(snapshot, playerReport);

        Assert.Contains("No hostile activity is currently known", report.CandidateEvidence, StringComparison.Ordinal);
        Assert.Contains("observed=current-turn", report.SelectedEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("No hostile activity is currently known", report.SelectedEvidence, StringComparison.Ordinal);
        Assert.Contains("CURRENT PLAYER REPORT", report.FusedInterpretation, StringComparison.Ordinal);
        Assert.Contains(playerReport, report.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("no eligible hostile", report.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lack of sensor corroboration", report.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("overcast", report.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, report.ModelContext.Split("radio dish", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void EvidenceFusion_ExplicitConfirmationQuestionIncludesAbsenceWithoutContradictingReport()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, contacts: 0);
        repository.Remember("Enemy movement was reported at the radio dish in the south corner.", "user-reported", ["enemy", "radio-dish"]);
        const string question = "Does the own-side feed confirm enemy movement at the radio dish?";
        string snapshot = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build(question));

        TacticalEvidenceReport report = TacticalContextInterpreter.Analyze(snapshot, question);

        Assert.Contains("No hostile activity is currently known", report.SelectedEvidence, StringComparison.Ordinal);
        Assert.Contains("No hostile activity is currently known", report.ModelContext, StringComparison.Ordinal);
        Assert.Contains("Enemy movement was reported", report.ModelContext, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceSelection_WeatherAppearsOnlyForWeatherIntent()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        TacticalSnapshotBuilder builder = new(repository, repository, time);
        string reportSnapshot = JsonSerializer.Serialize(builder.Build("Be advised, there is a tank in grid 054."));
        string weatherSnapshot = JsonSerializer.Serialize(builder.Build("What is the weather?"));

        Assert.DoesNotContain("overcast", TacticalContextInterpreter.Interpret(reportSnapshot, "Be advised, there is a tank in grid 054."), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overcast", TacticalContextInterpreter.Interpret(weatherSnapshot, "What is the weather?"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnlyContextPreview_DoesNotChangeConversationReferents()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, groups: 2, contacts: 1);
        OperationalSnapshotBuilder builder = new(repository, time);

        builder.Build("Do we have friendlies nearby?");
        builder.BuildPreview("Where are the hostiles?");
        JsonElement followup = JsonDocument.Parse(JsonSerializer.Serialize(builder.Build("What unit is this?"))).RootElement.Clone();

        Assert.Equal("Alpha 1-2", followup.GetProperty("retrievedMemory").GetProperty("dialogueFocus")
            .GetProperty("friendlyGroupCallsign").GetString());
    }

    [Fact]
    public void ContactTrack_DisappearsToLastKnown_ReappearsWithSameIdentity_AndSurvivesRestart()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        string track;
        using (SqliteStateRepository repository = Ready(db.Path, time, contacts: 1))
        {
            track = Assert.Single(repository.GetContactTracks()).TrackId;
            Apply(repository, 2, contacts: 0);
            Assert.Equal("last-known", Assert.Single(repository.GetContactTracks()).Status);
        }
        time.Advance(TimeSpan.FromMinutes(2));
        using (SqliteStateRepository repository = new(db.Path, time))
        {
            Assert.Equal(track, Assert.Single(repository.GetContactTracks()).TrackId);
            repository.ApplyHandshake("mission-a", "session-b", "Altis", 30720, time.GetUtcNow());
            Apply(repository, 1, contacts: 1, sessionId: "session-b");
            MissionContactTrack updated = Assert.Single(repository.GetContactTracks());
            Assert.Equal(track, updated.TrackId);
            Assert.Equal("current", updated.Status);
            Assert.Equal(2, updated.ObservationCount);
        }
    }

    [Fact]
    public void ContactEligibility_ExcludesFriendlyCivilianNeutralUnknownAndStatic()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start), contacts: 0);
        JsonElement contacts = Json("""
        {"contacts":[
          {"sourceId":"friendly","class":"B_Soldier_F","displayName":"friendly","contactType":"person","perceivedSide":"WEST","relationship":"friendly","estimatedPosition":[1,2,0],"positionErrorMeters":1,"lastSeenAgeSeconds":1,"lastThreatAgeSeconds":1,"observerGroupSourceIds":[]},
          {"sourceId":"civilian","class":"C_man_1","displayName":"civilian","contactType":"person","perceivedSide":"CIV","relationship":"civilian","estimatedPosition":[1,2,0],"positionErrorMeters":1,"lastSeenAgeSeconds":1,"lastThreatAgeSeconds":1,"observerGroupSourceIds":[]},
          {"sourceId":"static","class":"Land_Lamp_F","displayName":"lamp","contactType":"other","perceivedSide":"EAST","relationship":"hostile","estimatedPosition":[1,2,0],"positionErrorMeters":1,"lastSeenAgeSeconds":1,"lastThreatAgeSeconds":1,"observerGroupSourceIds":[]}
        ]}
        """);
        repository.ApplySnapshot(Snapshot(2, new Dictionary<string, StateSnapshotSection>
        { ["knownContacts"] = Section("knownContacts", contacts) }));
        Assert.Empty(repository.GetContactTracks());
    }

    [Fact]
    public void Memory_IsSessionScopedFtsBackedAndPhysicalForgetRemovesIt()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        long id = repository.Remember("The western building is mined", "user-reported", ["mine"]);
        MissionMemoryEntry found = Assert.Single(repository.SearchMemory("western building"));
        Assert.Equal(id, found.Id); Assert.Equal("user-reported", found.Provenance);
        Assert.True(repository.ForgetMemory(id));
        Assert.Empty(repository.SearchMemory("western building"));
    }

    [Fact]
    public void DataReceiveMode_StoresFactsButRejectsQuestionsAndInsults()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);
        Assert.True(service.TryHandle("Are you ready to receive data?", out string ready));
        Assert.Equal("Ready. Send it.", ready);
        Assert.True(service.TryHandle("The western road is blocked.", out _));
        Assert.True(service.TryHandle("You're useless.", out string rejected));
        Assert.StartsWith("Not stored", rejected, StringComparison.Ordinal);
        Assert.Single(repository.SearchMemory("western road"));
        Assert.Empty(repository.SearchMemory("useless"));
    }

    [Fact]
    public void AutomaticTacticalReport_IsSavedSilentlyBeforeNormalAnswerContinues()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);

        bool handled = service.TryHandle("Be advised, there is a tank in grid 054.", out string response);

        Assert.False(handled);
        Assert.Equal(string.Empty, response);
        MissionMemoryEntry report = Assert.Single(repository.SearchMemory("tank 054"));
        Assert.Equal("user-reported", report.Provenance);
        Assert.Contains("player-report", report.Tags);
    }

    [Fact]
    public async Task AutomaticTacticalReport_IsAvailableInTheSameTurnSnapshot()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        WorldSnapshotBuilder snapshots = new(new WorldStateStore(time), time, stateRepository: repository);
        MissionMemoryConversationService memory = new(repository, time);
        CountingAssistant assistant = new();
        using AssistantTurnService turns = new(assistant,
            question => snapshots.TryBuildCurrentSituation(question, out string snapshot) ? (true, snapshot) : (false, string.Empty),
            _ => Task.FromResult(("key", "model", ResponseProfilePolicy.Defaults())),
            (_, _, _) => Task.FromResult("unused"));
        turns.SetLocalTurnHandler(text => memory.TryHandle(text, out string response) ? (true, response) : (false, string.Empty));

        await turns.SubmitUserTurnAsync("Be advised, there is a tank in grid 054.", UserTurnSource.Typed, CancellationToken.None);

        Assert.Equal(1, assistant.Calls);
        Assert.Contains("tank in grid 054", assistant.LastSnapshot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncompleteTacticalReport_IsHeldUntilLocationClarificationArrives()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);

        Assert.True(service.TryHandle("There is a tower 300 metres away.", out string clarification));
        Assert.Contains("grid", clarification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bearing", clarification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("describe", clarification, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.SearchMemory("tower"));

        Assert.False(service.TryHandle("Bearing 310, it is red metal and mission relevant.", out _));
        MissionMemoryEntry stored = Assert.Single(repository.SearchMemory("tower 310"));
        Assert.Contains("300 metres", stored.Text, StringComparison.Ordinal);
        Assert.Contains("red metal", stored.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticReportFilter_RejectsQuestionsHypotheticalsInsultsAndUnrelatedFacts()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);

        foreach (string text in new[]
        {
            "Is there a tank in grid 054?", "Maybe there is a hostile tank in grid 054.",
            "You are a useless idiot.", "Chocolate cake tastes excellent today.",
            "I care about excellent chocolate cake."
        })
            Assert.False(service.TryHandle(text, out _));
        Assert.Empty(repository.SearchMemory(string.Empty));
    }

    [Fact]
    public void DesiredAction_IsNotMisclassifiedAsAnObservationReport()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);

        Assert.False(service.TryHandle("I want to um storm the airport.", out string response));
        Assert.False(service.TryHandle("I have no idea.", out _));
        Assert.False(service.TryHandle("I don't have a bearing or range.", out _));

        Assert.Equal(string.Empty, response);
        Assert.Empty(repository.SearchMemory(string.Empty));
    }

    [Fact]
    public void MissingDetailInability_CancelsPendingReportWithoutClarificationLoop()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);

        Assert.True(service.TryHandle("There is a tower 300 metres away.", out string first));
        Assert.Contains("bearing", first, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.TryHandle("I don't have a bearing or range.", out string cancelled));
        Assert.Equal("Understood. The incomplete report was not stored.", cancelled);
        Assert.Empty(repository.SearchMemory(string.Empty));

        Assert.False(service.TryHandle("What is the current situation?", out string next));
        Assert.Equal(string.Empty, next);
    }

    [Fact]
    public void PunctuationFreeOperationalQuestion_IsNeverStoredAsAReport()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        MissionMemoryConversationService service = new(repository);

        Assert.False(service.TryHandle("Any movement at the airport.", out string response));

        Assert.Equal(string.Empty, response);
        Assert.Empty(repository.SearchMemory(string.Empty));
    }

    [Fact]
    public void TentativeNamedLocationReport_ConfirmsOnceAndStoresWithoutLocationLoop()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        ApplyAirportPicture(repository, contacts: 0, time.GetUtcNow());
        MissionMemoryConversationService service = new(repository, time);

        Assert.True(service.TryHandle("It seems there is enemy movement at the Airport.", out string confirmation));
        Assert.Equal("Copy—you report enemy movement at the Airport. Nothing else currently confirms it. Can you confirm?", confirmation);
        Assert.Empty(repository.SearchMemory(string.Empty));

        Assert.True(service.TryHandle("Affirmative.", out string stored));
        Assert.Equal("Copy. Report logged.", stored);
        MissionMemoryEntry report = Assert.Single(repository.SearchMemory("enemy movement Airport"));
        Assert.Equal("Enemy movement at the Airport.", report.Text);
        Assert.Contains("explicitly-confirmed", report.Tags);
    }

    [Fact]
    public void SemanticArea_FusesOnlyEligibleContactsIntoNaturalModelContext()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        ApplyAirportPicture(repository, contacts: 1, time.GetUtcNow());
        const string question = "Any movement at the airport.";
        string snapshot = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build(question));

        TacticalEvidenceReport report = TacticalContextInterpreter.Analyze(snapshot, question);

        Assert.Contains("Recent observations indicate hostile movement at the Airport.", report.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("You report:", report.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("known rectangular area", report.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1500", report.ModelContext, StringComparison.Ordinal);
        foreach (string internalTerm in new[] { "player-reported", "own-side", "mission-defined", "canonical", "database", "evidence", "provenance", "state mirror", "bounded picture", "telemetry", "contact track", "confidence", "freshness" })
            Assert.DoesNotContain(internalTerm, report.ModelContext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticLocationGeometry_UsesArmaHalfAxesRotationAndUncertainty()
    {
        SemanticLocationDefinition rectangle = new("Airport", new WorldPosition(1000, 1000, 0), "rectangle", 100, 20, 90, false);
        SemanticLocationDefinition ellipse = rectangle with { Shape = "ellipse", DirectionDegrees = 0 };

        Assert.True(SemanticLocationPolicy.Contains(rectangle, new WorldPosition(1000, 1090, 0), 0));
        Assert.False(SemanticLocationPolicy.Contains(rectangle, new WorldPosition(1090, 1000, 0), 0));
        Assert.True(SemanticLocationPolicy.Contains(rectangle, new WorldPosition(1030, 1000, 0), 10));
        Assert.True(SemanticLocationPolicy.Contains(ellipse, new WorldPosition(1090, 1005, 0), 0));
        Assert.False(SemanticLocationPolicy.Contains(ellipse, new WorldPosition(1090, 1015, 0), 0));
    }

    [Fact]
    public void TacticalSnapshot_ExportsOnlyTextBearingLocationsAndPreservesAreaGeometry()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        var sections = new Dictionary<string, StateSnapshotSection>
        {
            ["markers"] = Section("markers", Json(JsonSerializer.Serialize(new
            {
                markers = new object[]
                {
                    new { sourceId = "blank-internal-id", text = "", position = new[] { 1000d, 1000d, 0 }, type = "mil_dot", color = "ColorRed", shape = "ICON", size = new[] { 1d, 1d }, direction = 0, alpha = 1, polyline = Array.Empty<double>() },
                    new { sourceId = "area-internal-id", text = "Airport", position = new[] { 1500d, 1500d, 0 }, type = "Empty", color = "ColorBlack", shape = "RECTANGLE", size = new[] { 120d, 80d }, direction = 35, alpha = 1, polyline = Array.Empty<double>() },
                    new { sourceId = "point-internal-id", text = "Relay Site", position = new[] { 1800d, 1800d, 0 }, type = "mil_dot", color = "ColorBlue", shape = "ICON", size = new[] { 1d, 1d }, direction = 0, alpha = 1, polyline = Array.Empty<double>() }
                }
            })))
        };
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(
            new StateSnapshotMessage("locations", "mission-a", "session-a", 102, 2, false, time.GetUtcNow(), sections)).Status);

        JsonElement locations = JsonDocument.Parse(JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("Where is the Airport?")))
            .RootElement.GetProperty("markers").Clone();

        Assert.Equal(2, locations.GetProperty("count").GetInt32());
        JsonElement airport = locations.GetProperty("records").EnumerateArray().Single(record => record.GetProperty("text").GetString() == "Airport");
        Assert.Equal("RECTANGLE", airport.GetProperty("shape").GetString());
        Assert.Equal(new[] { 120d, 80d }, airport.GetProperty("size").EnumerateArray().Select(value => value.GetDouble()));
        Assert.DoesNotContain("internal-id", locations.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherContract_CollectsAndExposesOnlyOvercastToTheModel()
    {
        string sqf = File.ReadAllText(Path.Combine(Root(), "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishStateSnapshot.sqf"));
        string stateSchema = File.ReadAllText(Path.Combine(Root(), "schemas/state-snapshot-v2.schema.json"));
        string tacticalSchema = File.ReadAllText(Path.Combine(Root(), "schemas/tactical-snapshot-v2.schema.json"));
        string parser = File.ReadAllText(Path.Combine(Root(), "src/ArmaAiBridge.App/Services/StateSnapshotParser.cs"));
        string operational = File.ReadAllText(Path.Combine(Root(), "src/ArmaAiBridge.App/Services/OperationalSnapshotBuilder.cs"));

        foreach (string removed in new[] { "ambientTemperature", "temperatureCelsius", "windDirection", "windStrength", "\"wind\"", "\"gusts\"" })
        {
            Assert.DoesNotContain(removed, sqf, StringComparison.Ordinal);
            Assert.DoesNotContain(removed, stateSchema, StringComparison.Ordinal);
            Assert.DoesNotContain(removed, parser, StringComparison.Ordinal);
            Assert.DoesNotContain(removed, operational, StringComparison.Ordinal);
        }
        Assert.Contains("\"overcast\"", tacticalSchema, StringComparison.Ordinal);
        Assert.Contains("\"condition\"", tacticalSchema, StringComparison.Ordinal);
        foreach (string removed in new[] { "temperature", "wind", "rain", "fog" })
            Assert.DoesNotContain($"\"{removed}\"", tacticalSchema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangedStateReport_UpdatesOneMatchingEntryAndNewSessionClearsPlayerMemory()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time);
        MissionMemoryConversationService service = new(repository);
        Assert.False(service.TryHandle("Be advised, a red tower is located in grid 69.", out _));
        long originalId = Assert.Single(repository.SearchMemory("tower grid 69")).Id;

        Assert.False(service.TryHandle("The tower I saw earlier in grid 69 is destroyed.", out _));
        MissionMemoryEntry updated = Assert.Single(repository.SearchMemory("tower grid 69"));
        Assert.Equal(originalId, updated.Id);
        Assert.Contains("destroyed", updated.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corrected", updated.Tags);

        Assert.Equal(TelemetryIngestStatus.Applied,
            repository.ApplyHandshake("mission-a", "session-b", "Altis", 30720, time.GetUtcNow()).Status);
        Assert.Empty(repository.SearchMemory(string.Empty));
    }

    [Fact]
    public void Lore_PreservesScopeEnablementAndBounds()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start));
        repository.SaveLoreSection("Mission", "The western road is blocked.", true, false);
        repository.SaveLoreSection("Common", "Papa Bear knows the campaign history.", false, true);
        Assert.Equal(2, repository.GetLoreSections().Count);
        Assert.Throws<InvalidOperationException>(() => repository.SaveLoreSection("Map", new string('x', 2001), true, false));
        string json = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository).Build("western road"));
        Assert.Contains("western road is blocked", json, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign history", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiringSolutionRequest_IsLocalAndNeverCallsOpenAiOrTools()
    {
        CountingAssistant assistant = new();
        using AssistantTurnService turns = new(assistant, () => (true, "{}"),
            _ => Task.FromResult(("key", "model", ResponseProfilePolicy.Defaults())),
            (_, _, _) => throw new InvalidOperationException("No tool expected"), () => "Alpha 1-1");
        AssistantResponse response = await turns.SubmitUserTurnAsync("Give me a firing solution.", UserTurnSource.Typed, CancellationToken.None);
        Assert.Equal("Alpha 1-1, firing-solution calculation is not available.", response.Text);
        Assert.Equal(0, response.ToolCalls); Assert.Equal(0, assistant.Calls);
    }

    [Fact]
    public void OnlyStrictMemoryToolsRemainModelFacing()
    {
        string source = File.ReadAllText(Path.Combine(Root(), "src/ArmaAiBridge.App/Services/OpenAiAssistantService.cs"));
        foreach (string retained in new[] { "remember_information", "search_memory", "update_memory", "forget_memory" }) Assert.Contains(retained, source, StringComparison.Ordinal);
        foreach (string removed in new[] { "query_environment", "query_state", "find_named_locations", "query_friendly_forces", "query_assets", "query_mission_capabilities", "calculate_firing_solution" })
            Assert.DoesNotContain($"\"{removed}\"", source, StringComparison.Ordinal);
        Assert.Contains("[\"additionalProperties\"] = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextOnDemand_AttachesContextAndMemoryToolsWithoutKeywordRouting()
    {
        CaptureHandler handler = new();
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using OpenAiAssistantService service = new(client);
        const string snapshot = "{\"schema\":\"arma-ai-bridge/context-seed-state-v1\",\"player\":{\"side\":\"WEST\"}}";
        AssistantResponse response = await service.AskAsync("key", "gpt-5-mini", "Remember that the road is blocked.", snapshot,
            ResponseProfilePolicy.Defaults(), (_, _, _) => Task.FromResult("unused"), CancellationToken.None);
        JsonElement[] tools = handler.Request!.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(9, tools.Length);
        Assert.Equal(new[]
        {
            "inspect_context_catalogue", "query_context",
            "query_long_term_map_intelligence", "record_player_information",
            "record_event_assessment",
            "remember_information", "search_memory", "update_memory", "forget_memory"
        }, tools.Select(x => x.GetProperty("name").GetString()));
        Assert.All(tools, tool => Assert.False(tool.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean()));
        Assert.Equal(9, response.RequestMetrics!.SelectedToolCount);
    }

    [Fact]
    public void TacticalSchema_ClosesRootAndEveryObjectSchema()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "schemas/tactical-snapshot-v2.schema.json")));
        AssertClosed(schema.RootElement, "$root");
    }

    [Fact]
    public void DialogueFocus_PreservesNearestFriendlyAndGeneralImmediateCorrection()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start), groups: 2, contacts: 1);
        TacticalSnapshotBuilder builder = new(repository, repository);
        builder.Build("Do we have any friendlies nearby?");
        JsonElement followup = JsonDocument.Parse(JsonSerializer.Serialize(builder.Build("What unit is this?"))).RootElement;
        string callsign = followup.GetProperty("retrievedMemory").GetProperty("dialogueFocus").GetProperty("friendlyGroupCallsign").GetString()!;
        Assert.NotEqual("Alpha 1-1", callsign);
        builder.Build("Where are the hostages?");
        JsonElement corrected = JsonDocument.Parse(JsonSerializer.Serialize(builder.Build("Hostiles, not hostages."))).RootElement;
        Assert.Contains("Hostiles", corrected.GetProperty("retrievedMemory").GetProperty("dialogueFocus").GetProperty("resolvedQuestion").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContactGrouping_IsDeterministicAndFourNearbyInfantryFormOneGroup()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start), contacts: 4);
        TacticalSnapshotBuilder builder = new(repository, repository);
        string first = JsonSerializer.Serialize(builder.Build("hostiles"));
        string second = JsonSerializer.Serialize(builder.Build("hostiles"));
        JsonElement group = JsonDocument.Parse(first).RootElement.GetProperty("enemyContacts").GetProperty("groups")[0].Clone();
        Assert.Equal(4, group.GetProperty("memberCount").GetInt32());
        Assert.Matches("^[0-9]{6}$", group.GetProperty("grid").GetString()!);
        Assert.Equal(JsonDocument.Parse(second).RootElement.GetProperty("enemyContacts").GetProperty("groups")[0].GetProperty("estimatedCentroid").GetRawText(), group.GetProperty("estimatedCentroid").GetRawText());
    }

    [Fact]
    public void ContactProjection_DoesNotExposePlayerRelativeMovementRangeBearingOrDirection()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, contacts: 1);
        time.Advance(TimeSpan.FromSeconds(10));
        ApplyObservation(repository, 2, new WorldPosition(1200, 1200, 0), new WorldPosition(1300, 1300, 0), time.GetUtcNow());
        JsonElement contact = JsonDocument.Parse(JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("Are they closing?")))
            .RootElement.GetProperty("enemyContacts").GetProperty("records")[0].Clone();
        foreach (string forbidden in new[] { "movementTrend", "estimatedRelativeSpeedMetersPerSecond", "movementConfidence", "rangeFromPlayerMeters", "bearingFromPlayerDegrees", "direction" })
            Assert.False(contact.TryGetProperty(forbidden, out _), forbidden);
    }

    [Fact]
    public void ExplicitDeathAndForget_AreDeterministicAndForgetCascadesObservations()
    {
        using TestDatabase db = new();
        using SqliteStateRepository repository = Ready(db.Path, new ManualTimeProvider(Start), contacts: 1);
        MissionContactTrack track = Assert.Single(repository.GetContactTracks());
        Assert.True(repository.MarkContactDead(track.TrackId));
        Assert.Equal("dead", Assert.Single(repository.GetContactTracks()).Status);
        Assert.True(repository.ForgetContact(track.TrackId));
        Assert.Empty(repository.GetContactTracks());
        Assert.Empty(repository.GetContactObservations(track.TrackId));
    }

    [Fact]
    public void MaximumSnapshot_UsesExplicitDeterministicTruncation()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, groups: 128, contacts: 256, markers: 256);
        for (int i = 0; i < 12; i++) repository.Remember("situation " + new string((char)('a' + i), 1990), "user-reported");
        foreach (string scope in new[] { "Mission", "Map", "Player", "Target", "Common" })
            repository.SaveLoreSection(scope, new string(scope[0], 2000), true, true);
        WorldSnapshotBuilder snapshots = new(new WorldStateStore(time), time, stateRepository: repository);
        Assert.True(snapshots.TryBuildCurrentSituation("situation", out string json));
        Assert.True(Encoding.UTF8.GetByteCount(json) <= TacticalSnapshotBuilder.MaximumPayloadBytes);
        using JsonDocument document = JsonDocument.Parse(json);
        bool truncated = document.RootElement.GetProperty("modelPayloadTruncated").GetBoolean();
        JsonElement counts = document.RootElement.GetProperty("includedCounts").GetProperty("enemyContacts");
        if (truncated) Assert.True(counts.GetProperty("included").GetInt32() < counts.GetProperty("original").GetInt32());
        else Assert.Equal(counts.GetProperty("original").GetInt32(), counts.GetProperty("included").GetInt32());
        Assert.Equal(128, document.RootElement.GetProperty("friendlyForces").GetProperty("groups").GetArrayLength());
    }

    private static SqliteStateRepository Ready(string path, ManualTimeProvider time, int groups = 0, int contacts = 0, int markers = 0)
    {
        SqliteStateRepository repository = new(path, time);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplyHandshake("mission-a", "session-a", "Altis", 30720, time.GetUtcNow()).Status);
        Apply(repository, 1, groups, contacts, markers);
        return repository;
    }

    private static void Apply(SqliteStateRepository repository, long sequence, int groups = 0, int contacts = 0, int markers = 0, string sessionId = "session-a")
    {
        var sections = new Dictionary<string, StateSnapshotSection>
        {
            ["player"] = Section("player", Json("""{"sourceId":"player","side":"WEST","groupSourceId":"self-group","groupCallsign":"Alpha 1-1","positionATL":[1000,1000,0],"positionASL":[1000,1000,0],"grid":"010010"}""")),
            ["environment"] = Section("environment", Json("""{"overcast":0.4,"forecastOvercast":0.4,"rain":0,"fog":0.05,"fogParameters":[0.05,0,0],"forecastFog":0.05,"waves":0,"lightning":0,"humidity":0,"nextWeatherChange":0}""")),
            ["timeAstronomy"] = Section("timeAstronomy", Json("""{"missionDate":[2026,7,22,12,0],"daytime":12,"elapsedMissionTime":10,"timeMultiplier":1,"moonPhase":0,"sunOrMoon":1}""")),
            ["friendlyForces"] = Section("friendlyForces", Friendly(groups)),
            ["knownContacts"] = Section("knownContacts", Contacts(contacts)),
            ["markers"] = Section("markers", Markers(markers))
        };
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Snapshot(sequence, sections, sessionId)).Status);
    }

    private static StateSnapshotMessage Snapshot(long sequence, IReadOnlyDictionary<string, StateSnapshotSection> sections, string sessionId = "session-a")
        => new($"message-{sequence}", "mission-a", sessionId, 100 + sequence, sequence, true, Start.AddSeconds(sequence), sections);
    private static StateSnapshotSection Section(string name, JsonElement payload)
        => new(name, StateSectionReadiness.Ready, 100, payload);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private static JsonElement Friendly(int count) => Json(JsonSerializer.Serialize(new
    {
        groups = Enumerable.Range(0, count).Select(i => new { sourceId = $"g-{i}", callsign = $"Alpha 1-{i + 2}", leaderSourceId = $"u-{i}", memberSourceIds = new[] { $"u-{i}" }, leaderPosition = new[] { 1104.4 + i * 25, 1206.6 + i * 25, 0 }, leaderSpeedKph = 0, behaviour = "AWARE", combatMode = "YELLOW", formation = "WEDGE", assignedTargetSourceIds = Array.Empty<string>() }),
        units = Enumerable.Range(0, count).Select(i => new { sourceId = $"u-{i}", groupSourceId = $"g-{i}", @class = "B_Soldier_F", displayRole = "rifleman", position = new[] { 1104.4 + i * 25, 1206.6 + i * 25, 0 }, alive = true, lifeState = "HEALTHY", mobile = true, damage = 0, currentCommand = "MOVE", assignedTargetSourceId = "", vehicleSourceId = "", vehicleRole = "" })
    }));
    private static JsonElement Contacts(int count) => Json(JsonSerializer.Serialize(new
    {
        contacts = Enumerable.Range(0, count).Select(i => new { sourceId = $"c-{i}", @class = "O_Soldier_F", displayName = "Rifleman", contactType = "person", perceivedSide = "EAST", relationship = "hostile", estimatedPosition = new[] { 1500d + i * 8, 1500d + i * 8, 0 }, positionErrorMeters = 15, lastSeenAgeSeconds = 2, lastThreatAgeSeconds = 3, observerGroupSourceIds = new[] { "g-0" } })
    }));
    private static JsonElement Markers(int count) => Json(JsonSerializer.Serialize(new
    {
        markers = Enumerable.Range(0, count).Select(i => new { sourceId = $"m-{i}", text = new string('M', 150) + i, position = new[] { 1300d + i, 1300d + i, 0 }, type = "mil_dot", color = "ColorRed", shape = "ICON", size = new[] { 1, 1 }, direction = 45, alpha = 1, channel = 1, polyline = Array.Empty<double>() })
    }));
    private static void ApplyObservation(SqliteStateRepository repository, long sequence, WorldPosition player, WorldPosition contact, DateTimeOffset received)
    {
        var sections = new Dictionary<string, StateSnapshotSection>
        {
            ["player"] = Section("player", Json(JsonSerializer.Serialize(new { sourceId = "player", side = "WEST", groupSourceId = "self-group", groupCallsign = "Alpha 1-1", positionATL = new[] { player.X, player.Y, player.Z }, positionASL = new[] { player.X, player.Y, player.Z }, grid = "010010" }))),
            ["knownContacts"] = Section("knownContacts", Json(JsonSerializer.Serialize(new { contacts = new[] { new { sourceId = "c-0", @class = "O_Soldier_F", displayName = "Rifleman", contactType = "person", perceivedSide = "EAST", relationship = "hostile", estimatedPosition = new[] { contact.X, contact.Y, contact.Z }, positionErrorMeters = 5, lastSeenAgeSeconds = 0, lastThreatAgeSeconds = 0, observerGroupSourceIds = new[] { "g-0" } } } })))
        };
        StateSnapshotMessage snapshot = new($"message-{sequence}", "mission-a", "session-a", 100 + sequence, sequence, false, received, sections);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(snapshot).Status);
    }
    private static void ApplyAirportPicture(SqliteStateRepository repository, int contacts, DateTimeOffset received)
    {
        var sections = new Dictionary<string, StateSnapshotSection>
        {
            ["markers"] = Section("markers", Json(JsonSerializer.Serialize(new
            {
                markers = new[] { new { sourceId = "mission-airport", text = "Airport", position = new[] { 1500d, 1500d, 0 }, type = "Empty", color = "ColorBlack", shape = "RECTANGLE", size = new[] { 100d, 80d }, direction = 0, alpha = 1, channel = 1, polyline = Array.Empty<double>() } }
            }))),
            ["knownContacts"] = Section("knownContacts", Contacts(contacts))
        };
        StateSnapshotMessage snapshot = new("message-airport", "mission-a", "session-a", 102, 2, false, received, sections);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(snapshot).Status);
    }
    private static string Root() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    private static void AssertClosed(JsonElement node, string path)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            {
                Assert.True(node.TryGetProperty("additionalProperties", out JsonElement additional), path);
                Assert.Equal(JsonValueKind.False, additional.ValueKind);
            }
            foreach (JsonProperty property in node.EnumerateObject()) AssertClosed(property.Value, path + "." + property.Name);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in node.EnumerateArray()) AssertClosed(item, path + "[]");
    }

    private sealed class CountingAssistant : IOpenAiAssistantService
    {
        public int Calls { get; private set; }
        public string LastSnapshot { get; private set; } = string.Empty;
        public Task<AssistantResponse> AskAsync(string apiKey, string model, string question, string worldSnapshotJson, ResponseProfileSettings responseProfile, Func<string, JsonElement, CancellationToken, Task<string>> executeTool, CancellationToken cancellationToken)
        { Calls++; LastSnapshot = worldSnapshotJson; return Task.FromResult(new AssistantResponse("unexpected", model, 0, 0, 0)); }
        public void ResetConversation() { }
        public void Dispose() { }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public JsonDocument? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"completed\",\"model\":\"gpt-5-mini\",\"output\":[{\"type\":\"message\",\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\"Stored.\"}]}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}", Encoding.UTF8, "application/json")
            };
        }
        protected override void Dispose(bool disposing) { if (disposing) Request?.Dispose(); base.Dispose(disposing); }
    }

    private sealed class TestDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arma-tactical-{Guid.NewGuid():N}.db");
        public void Dispose() { foreach (string path in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(path)) File.Delete(path); }
    }
}
