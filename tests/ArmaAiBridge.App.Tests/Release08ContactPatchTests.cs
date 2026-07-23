using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Release08ContactPatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitReportedGrids_AreSessionScopedAndRelatedWithoutCanonicalPlayerPosition()
    {
        using TestDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = new(database.Path, time);
        Assert.Equal(TelemetryIngestStatus.Applied,
            repository.ApplyHandshake("mission-a", "session-a", "Altis", 30720, Start).Status);
        MissionMemoryConversationService conversation = new(repository, time);

        Assert.True(conversation.TryHandle("My mission goal is in grid 027067.", out string goal));
        Assert.Contains("027067", goal, StringComparison.Ordinal);
        Assert.True(conversation.TryHandle("How far is it away from me?", out string missing));
        Assert.Contains("current six-digit grid", missing, StringComparison.OrdinalIgnoreCase);
        Assert.True(conversation.TryHandle("My current position is in 038084.", out string position));
        Assert.Contains("038084", position, StringComparison.Ordinal);
        Assert.True(conversation.TryHandle("How far am I from my mission goal?", out string relation));
        Assert.Contains("2 kilometres southwest", relation, StringComparison.OrdinalIgnoreCase);

        ReportedLocationAnchor stored = Assert.IsType<ReportedLocationAnchor>(repository.GetReportedLocation("reported-position"));
        Assert.Equal(new WorldPosition(3850, 8450, 0), stored.Position);
        Assert.InRange(stored.UncertaintyRadiusMeters, 70, 71);

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.True(conversation.TryHandle("How far am I from my mission goal?", out string stale));
        Assert.Contains("too old", stale, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(TelemetryIngestStatus.Applied,
            repository.ApplyHandshake("mission-a", "session-b", "Altis", 30720, time.GetUtcNow()).Status);
        Assert.Null(repository.GetReportedLocation("reported-position"));
        Assert.Null(repository.GetReportedLocation("mission-goal"));
    }

    [Fact]
    public void ReporterCallsign_IsPersistedAndInterpretedWithoutRawObserverIdentity()
    {
        using TestDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(database.Path, time, "EAST", "hostile");

        MissionContactTrack track = Assert.Single(repository.GetContactTracks());
        Assert.Equal(["Bravo 2-1"], track.ReporterCallsigns);
        string snapshot = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time)
            .Build("Who reported this enemy?"));
        TacticalEvidenceReport evidence = TacticalEvidencePipeline.Build(snapshot, "Who reported this enemy?");
        Assert.Contains("reported by Bravo 2-1", evidence.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("observer-group-raw", evidence.ModelContext, StringComparison.Ordinal);
        Assert.DoesNotContain("group-", evidence.ModelContext, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTargetKnowledge_IsAcceptedButFriendlyAndNeutralRemainOutsideTracks()
    {
        using TestDatabase database = new();
        using SqliteStateRepository repository = Ready(database.Path, new ManualTimeProvider(Start), "UNKNOWN", "unknown");
        MissionContactTrack track = Assert.Single(repository.GetContactTracks());
        Assert.Equal("unknown", track.Relationship);
        Assert.Equal("UNKNOWN", track.PerceivedSide);
        Assert.Equal("unknown infantry", track.Description);
    }

    [Fact]
    public void ContactAnnouncements_SilenceBaselineDeduplicateAndResetPerSession()
    {
        ManualTimeProvider time = new(Start);
        ContactAnnouncementDetector detector = new(new FixedPositionReporter(), time);
        MissionContactTrack first = Track("contact-a", "hostile", "current", 2550, 6450);
        Assert.Empty(detector.Evaluate("session-a", 1, [first], "Alpha 1-1"));
        Assert.Empty(detector.Evaluate("session-a", 2, [first], "Alpha 1-1"));

        MissionContactTrack unknown = Track("contact-b", "unknown", "current", 3050, 7050);
        ContactAnnouncement added = Assert.Single(detector.Evaluate("session-a", 3, [first, unknown], "Alpha 1-1"));
        Assert.Equal("new", added.Kind);
        Assert.Equal("Alpha 1-1, unknown infantry, 600 metres northeast of Bullseye Alpha.", added.VisibleText);
        Assert.DoesNotContain("Papa Bear", added.VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Over", added.VisibleText, StringComparison.OrdinalIgnoreCase);

        MissionContactTrack stale = first with { Status = "last-known" };
        Assert.Empty(detector.Evaluate("session-a", 4, [stale, unknown], "Alpha 1-1"));
        time.Advance(TimeSpan.FromSeconds(31));
        ContactAnnouncement reacquired = Assert.Single(detector.Evaluate("session-a", 5, [first, unknown], "Alpha 1-1"));
        Assert.Equal("reacquired", reacquired.Kind);
        Assert.Equal("Alpha 1-1, previously reported enemy infantry reacquired, 600 metres northeast of Bullseye Alpha.", reacquired.VisibleText);
        Assert.DoesNotContain("bearing", reacquired.VisibleText, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(detector.Evaluate("session-b", 1, [first, unknown], "Alpha 1-1"));
    }

    [Fact]
    public void ContactAnnouncements_GroupCompatibleTracksIntoOneRadioMessage()
    {
        ManualTimeProvider time = new(Start);
        ContactAnnouncementDetector detector = new(new FixedPositionReporter(), time);
        MissionContactTrack[] stale = Enumerable.Range(0, 4)
            .Select(index => Track($"contact-{index}", "hostile", "last-known", 6250 + index * 5, 5550 + index * 5))
            .ToArray();
        Assert.Empty(detector.Evaluate("session-a", 1, stale, "Alpha 1-1"));

        time.Advance(TimeSpan.FromSeconds(31));
        MissionContactTrack[] current = stale.Select(track => track with { Status = "current" }).ToArray();
        ContactAnnouncement announcement = Assert.Single(
            detector.Evaluate("session-a", 2, current, "Alpha 1-1"));

        Assert.Equal("reacquired", announcement.Kind);
        Assert.Equal("Alpha 1-1, previously reported enemy infantry group, approximately four contacts reacquired, 600 metres northeast of Bullseye Alpha.", announcement.VisibleText);
    }

    [Fact]
    public void ContactAnnouncements_ConsolidateRelatedInfantryAndVehicle()
    {
        ContactAnnouncementDetector detector = new(new FixedPositionReporter(), new ManualTimeProvider(Start));
        Assert.Empty(detector.Evaluate("session-a", 1, [], "Alpha 1-1"));
        MissionContactTrack infantry = Track("contact-a", "hostile", "current", 1000, 1000);
        MissionContactTrack vehicle = Track("contact-b", "hostile", "current", 1020, 1020) with
        {
            ContactType = "ground-vehicle",
            Description = "hostile vehicle"
        };
        ContactAnnouncement announcement = Assert.Single(
            detector.Evaluate("session-a", 2, [infantry, vehicle], "Alpha 1-1"));
        Assert.Equal(
            "Alpha 1-1, enemy infantry and vehicle, approximately two contacts, 600 metres northeast of Bullseye Alpha.",
            announcement.VisibleText);
    }

    [Fact]
    public void ContactAnnouncements_GroupDistinctVehicleTracksWithTheSameRadioPosition()
    {
        ContactAnnouncementDetector detector = new(new FixedPositionReporter(), new ManualTimeProvider(Start));
        Assert.Empty(detector.Evaluate("session-a", 1, [], "Alpha 1-2"));
        MissionContactTrack first = Track("vehicle-a", "hostile", "current", 1000, 1000) with
        {
            ContactType = "ground-vehicle",
            Description = "hostile vehicle"
        };
        MissionContactTrack second = Track("vehicle-b", "hostile", "current", 1200, 1200) with
        {
            ContactType = "ground-vehicle",
            Description = "hostile vehicle"
        };
        MissionContactTrack aircraft = Track("aircraft-a", "unknown", "current", 1400, 1400) with
        {
            ContactType = "air",
            Description = "unknown aircraft"
        };

        IReadOnlyList<ContactAnnouncement> announcements =
            detector.Evaluate("session-a", 2, [first, second, aircraft], "Alpha 1-2");

        Assert.Equal(2, announcements.Count);
        Assert.Contains(announcements, item =>
            item.VisibleText ==
            "Alpha 1-2, enemy vehicle group, two vehicles, 600 metres northeast of Bullseye Alpha.");
        Assert.Contains(announcements, item =>
            item.VisibleText ==
            "Alpha 1-2, unknown aircraft, 600 metres northeast of Bullseye Alpha.");
    }

    [Fact]
    public void ContactAnnouncements_DoNotCallABriefLastKnownFlickerAReacquisition()
    {
        ManualTimeProvider time = new(Start);
        ContactAnnouncementDetector detector = new(new FixedPositionReporter(), time);
        MissionContactTrack current = Track("contact-a", "hostile", "current", 2550, 6450);
        Assert.Empty(detector.Evaluate("session-a", 1, [current], "Alpha 1-1"));
        Assert.Empty(detector.Evaluate("session-a", 2, [current with { Status = "last-known" }], "Alpha 1-1"));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(detector.Evaluate("session-a", 3, [current], "Alpha 1-1"));
    }

    [Fact]
    public void OrdinaryHostileQuestion_ContainsCountsButNoPlayerRelativeFacts()
    {
        using TestDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(database.Path, time, "EAST", "hostile");
        string snapshot = JsonSerializer.Serialize(new TacticalSnapshotBuilder(repository, repository, time).Build("Do we have hostiles?"));
        TacticalEvidenceReport evidence = TacticalEvidencePipeline.Build(snapshot, "Do we have hostiles?");
        Assert.Contains("Current hostile picture: 1 current", evidence.ModelContext, StringComparison.Ordinal);
        foreach (string forbidden in new[] { "metres from your position", "bearing", "rangeFromPlayer", "bearingFromPlayer", "direction" })
            Assert.DoesNotContain(forbidden, evidence.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reported by", evidence.ModelContext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LastKnownEnemyPositionQuestion_SelectsTheNewestContactPosition()
    {
        using TestDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(database.Path, time, "EAST", "hostile");
        string snapshot = JsonSerializer.Serialize(
            new TacticalSnapshotBuilder(repository, repository, time)
                .Build("Can you give me the last known position of the enemies?"));

        TacticalEvidenceReport evidence = TacticalEvidencePipeline.Build(
            snapshot,
            "Can you give me the last known position of the enemies?");

        Assert.Contains("hostile infantry", evidence.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grid 025064", evidence.ModelContext, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status current", evidence.ModelContext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AiContextReset_DeletesRetainedLocalContextAndClearsDialogueFocus()
    {
        using TestDatabase database = new();
        ManualTimeProvider time = new(Start);
        using SqliteStateRepository repository = Ready(database.Path, time, "EAST", "hostile");
        repository.Remember("Enemy activity was reported at the airport.", "user-reported", ["enemy", "airport"]);
        repository.SaveLoreSection("Mission", "The airport is the current objective.", true, true);
        repository.SaveReportedLocation(new ReportedLocationAnchor(
            "mission-goal", "mission goal", "027067", new WorldPosition(2750, 6750, 0), 71, Start));
        TacticalSnapshotBuilder builder = new(repository, repository, time);
        JsonElement focused = JsonDocument.Parse(JsonSerializer.Serialize(builder.Build("Who reported this enemy?")))
            .RootElement.GetProperty("retrievedMemory").GetProperty("dialogueFocus").Clone();
        Assert.Equal(JsonValueKind.String, focused.GetProperty("hostileContactReference").ValueKind);

        builder.ResetDialogueFocus();
        JsonElement clearedFocus = JsonDocument.Parse(JsonSerializer.Serialize(
                builder.Build("What is the weather?", commitDialogueFocus: false)))
            .RootElement.GetProperty("retrievedMemory").GetProperty("dialogueFocus").Clone();
        Assert.Equal(JsonValueKind.Null, clearedFocus.GetProperty("hostileContactReference").ValueKind);

        repository.ResetCache();
        Assert.Equal(0, repository.GetDiagnostics().LastSequence);
        Assert.Empty(repository.GetContactTracks());
        Assert.Empty(repository.SearchMemory(string.Empty));
        Assert.Empty(repository.GetLoreSections());
        Assert.Null(repository.GetReportedLocation("mission-goal"));
    }

    private static SqliteStateRepository Ready(string path, ManualTimeProvider time, string perceivedSide, string relationship)
    {
        SqliteStateRepository repository = new(path, time);
        Assert.Equal(TelemetryIngestStatus.Applied,
            repository.ApplyHandshake("mission-a", "session-a", "Altis", 30720, Start).Status);
        Dictionary<string, StateSnapshotSection> sections = new()
        {
            ["player"] = Section("player", Json("""{"sourceId":"player","side":"WEST","groupSourceId":"self-group","groupCallsign":"Alpha 1-1","positionATL":[1000,1000,0],"positionASL":[1000,1000,0],"grid":"010010"}""")),
            ["friendlyForces"] = Section("friendlyForces", Json("""{"groups":[{"sourceId":"observer-group-raw","callsign":"Bravo 2-1","leaderSourceId":"observer-unit","memberSourceIds":["observer-unit"],"leaderPosition":[1200,1200,0],"leaderSpeedKph":0,"behaviour":"AWARE","combatMode":"YELLOW","formation":"WEDGE","assignedTargetSourceIds":[]}],"units":[{"sourceId":"observer-unit","groupSourceId":"observer-group-raw","class":"B_Soldier_F","displayRole":"rifleman","position":[1200,1200,0],"alive":true,"lifeState":"HEALTHY","mobile":true,"damage":0,"currentCommand":"MOVE","assignedTargetSourceId":"","vehicleSourceId":"","vehicleRole":""}]}""")),
            ["knownContacts"] = Section("knownContacts", Json(JsonSerializer.Serialize(new
            {
                contacts = new[] { new { sourceId = "contact-raw", @class = "O_Soldier_F", displayName = "Rifleman", contactType = "person", perceivedSide, relationship, estimatedPosition = new[] { 2550d, 6450d, 0 }, positionErrorMeters = 10, lastSeenAgeSeconds = 0, lastThreatAgeSeconds = 0, observerGroupSourceIds = new[] { "observer-group-raw" } } }
            })))
        };
        Assert.Equal(TelemetryIngestStatus.Applied,
            repository.ApplySnapshot(new StateSnapshotMessage("message-1", "mission-a", "session-a", 1, 1, true, Start, sections)).Status);
        return repository;
    }

    private static MissionContactTrack Track(string id, string relationship, string status, double x, double y)
        => new(id, "person", $"{relationship} infantry", relationship == "unknown" ? "UNKNOWN" : "EAST",
            relationship, status, Start, Start, Start, new WorldPosition(x, y, 0), 10, 1, false, ["Bravo 2-1"]);
    private static StateSnapshotSection Section(string name, JsonElement payload)
        => new(name, StateSectionReadiness.Ready, 1, payload);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class FixedPositionReporter : ITacticalPositionReporter
    {
        public TacticalPositionDescription Describe(WorldPosition target)
            => new("600 metres northeast of Bullseye Alpha",
                TacticalPositionReportingService.Grid(target), "bullseye", "Bullseye Alpha");
    }

    private sealed class TestDatabase : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arma-contact-patch-{Guid.NewGuid():N}.db");
        public void Dispose()
        {
            foreach (string path in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
