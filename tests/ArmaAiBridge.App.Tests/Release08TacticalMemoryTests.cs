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
    public void Memory_IsMissionScopedFtsBackedAndPhysicalForgetRemovesIt()
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
    public async Task MemoryIntent_RequestCapture_AttachesExactlyFourClosedTools()
    {
        CaptureHandler handler = new();
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        using OpenAiAssistantService service = new(client);
        const string snapshot = "{\"schema\":\"arma-ai-bridge/tactical-snapshot-v2\",\"player\":{\"side\":\"WEST\"},\"environment\":{},\"time\":{},\"friendlyForces\":{},\"enemyContacts\":{},\"markers\":{},\"retrievedMemory\":{},\"lore\":{},\"modelPayloadTruncated\":false,\"includedCounts\":{}}";
        AssistantResponse response = await service.AskAsync("key", "gpt-5-mini", "Remember that the road is blocked.", snapshot,
            ResponseProfilePolicy.Defaults(), (_, _, _) => Task.FromResult("unused"), CancellationToken.None);
        JsonElement[] tools = handler.Request!.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(4, tools.Length);
        Assert.Equal(new[] { "remember_information", "search_memory", "update_memory", "forget_memory" }, tools.Select(x => x.GetProperty("name").GetString()));
        Assert.All(tools, tool => Assert.False(tool.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean()));
        Assert.Equal(4, response.RequestMetrics!.SelectedToolCount);
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
    public void MovementTrend_UsesObservationAndPlayerHistory()
    {
        using TestDatabase db = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(db.Path, time, contacts: 1);
        time.Advance(TimeSpan.FromSeconds(10));
        ApplyObservation(repository, 2, new WorldPosition(1200, 1200, 0), new WorldPosition(1300, 1300, 0), time.GetUtcNow());
        JsonElement contact = JsonDocument.Parse(JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("Are they closing?")))
            .RootElement.GetProperty("enemyContacts").GetProperty("records")[0].Clone();
        Assert.Equal("closing", contact.GetProperty("movementTrend").GetString());
        Assert.True(contact.GetProperty("estimatedRelativeSpeedMetersPerSecond").GetDouble() > 0);
        Assert.Equal("medium", contact.GetProperty("movementConfidence").GetString());
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
        Assert.True(document.RootElement.GetProperty("modelPayloadTruncated").GetBoolean());
        JsonElement counts = document.RootElement.GetProperty("includedCounts").GetProperty("enemyContacts");
        Assert.True(counts.GetProperty("included").GetInt32() < counts.GetProperty("original").GetInt32());
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
            ["environment"] = Section("environment", Json("""{"overcast":0.4,"forecastOvercast":0.4,"rain":0,"fog":0.05,"fogParameters":[0.05,0,0],"forecastFog":0.05,"wind":[3,4],"windDirection":0,"windStrength":0,"gusts":0,"waves":0,"lightning":0,"humidity":0,"temperatureCelsius":24.7,"nextWeatherChange":0}""")),
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
        groups = Enumerable.Range(0, count).Select(i => new { sourceId = $"g-{i}", callsign = $"Alpha 1-{i + 2}", leaderSourceId = $"u-{i}", memberSourceIds = new[] { $"u-{i}" }, leaderPosition = new[] { 1104.4 + i * 25, 1206.6 + i * 25, 0 }, behaviour = "AWARE", combatMode = "YELLOW", formation = "WEDGE", assignedTargetSourceIds = Array.Empty<string>() }),
        units = Enumerable.Range(0, count).Select(i => new { sourceId = $"u-{i}", groupSourceId = $"g-{i}", @class = "B_Soldier_F", displayRole = "rifleman", position = new[] { 1104.4 + i * 25, 1206.6 + i * 25, 0 }, alive = true, lifeState = "HEALTHY", mobile = true, damage = 0, currentCommand = "MOVE", assignedTargetSourceId = "", vehicleSourceId = "", vehicleRole = "" })
    }));
    private static JsonElement Contacts(int count) => Json(JsonSerializer.Serialize(new
    {
        contacts = Enumerable.Range(0, count).Select(i => new { sourceId = $"c-{i}", @class = "O_Soldier_F", displayName = "Rifleman", contactType = "person", perceivedSide = "EAST", relationship = "hostile", estimatedPosition = new[] { 1500d + i * 8, 1500d + i * 8, 0 }, positionErrorMeters = 15, lastSeenAgeSeconds = 2, lastThreatAgeSeconds = 3, observerGroupSourceIds = new[] { "g-0" } })
    }));
    private static JsonElement Markers(int count) => Json(JsonSerializer.Serialize(new
    {
        markers = Enumerable.Range(0, count).Select(i => new { sourceId = $"m-{i}", text = new string('M', 150) + i, position = new[] { 1300d + i, 1300d + i, 0 }, type = "mil_dot", color = "ColorRed", shape = "ICON", size = new[] { 1, 1 }, direction = 45, alpha = 1, polyline = Array.Empty<double>() })
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
        public Task<AssistantResponse> AskAsync(string apiKey, string model, string question, string worldSnapshotJson, ResponseProfileSettings responseProfile, Func<string, JsonElement, CancellationToken, Task<string>> executeTool, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new AssistantResponse("unexpected", model, 0, 0, 0)); }
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
