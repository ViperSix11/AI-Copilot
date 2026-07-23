using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class RobustContextOnDemandTests
{
    [Fact]
    public void PlayerInformationJournal_PreservesRawAndStructuredRecordsSeparately()
    {
        MemoryRepository repository = new();
        PlayerInformationJournal journal = new(repository);

        journal.RecordRaw("There is a damaged truck at the airfield.", UserTurnSource.Typed);
        using JsonDocument arguments = JsonDocument.Parse("""
        {
          "group":"entities",
          "category":"vehicles",
          "subject":"damaged truck",
          "summary":"A damaged truck was reported at the airfield.",
          "basis":"explicit",
          "confidence":"reported",
          "clarificationStatus":"unknown",
          "clarificationTopic":"truck condition",
          "clarificationReason":"The reporter could not identify the damage."
        }
        """);

        string result = journal.Execute(arguments.RootElement);

        Assert.Contains("\"ok\":true", result, StringComparison.Ordinal);
        Assert.Equal(3, repository.Entries.Count);
        Assert.Equal("There is a damaged truck at the airfield.", repository.Entries[0].Text);
        Assert.Equal("raw-player-message", repository.Entries[0].Provenance);
        Assert.Equal("player-interpreted-explicit", repository.Entries[1].Provenance);
        Assert.Contains("group:entities", repository.Entries[1].Tags);
        Assert.Contains("clarification:unknown", repository.Entries[2].Tags);
    }

    [Fact]
    public void PlayerInformationJournal_RejectsCrossGroupSemanticCategory()
    {
        PlayerInformationJournal journal = new(new MemoryRepository());
        using JsonDocument arguments = JsonDocument.Parse("""
        {
          "group":"resources",
          "category":"current_contacts",
          "subject":"contact",
          "summary":"A contact was reported.",
          "basis":"explicit",
          "confidence":"reported",
          "clarificationStatus":"none",
          "clarificationTopic":null,
          "clarificationReason":null
        }
        """);

        Assert.Throws<InvalidOperationException>(() => journal.Execute(arguments.RootElement));
    }

    [Fact]
    public void MissionEventCoordinator_HeadsUpThenWaitsTwoSnapshotsForDevelopedReport()
    {
        MissionEventCoordinator coordinator = new();
        Dictionary<string, string> signatures = new() { ["contacts"] = "one" };
        ContactAnnouncement candidate = new("contact-one", "new", "012034", "unused");
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-23T12:00:00Z");

        string first = Assert.Single(coordinator.Observe(
            "mission-one", 10, [candidate], signatures, now));
        IReadOnlyList<string> second = coordinator.Observe(
            "mission-one", 11, [], signatures, now.AddSeconds(4));
        string third = Assert.Single(coordinator.Observe(
            "mission-one", 12, [], signatures, now.AddSeconds(8)));

        Assert.Contains("\"transition\":\"possible-new\"", first, StringComparison.Ordinal);
        Assert.Contains("\"windowSnapshotCount\":1", first, StringComparison.Ordinal);
        Assert.Empty(second);
        Assert.Contains("\"transition\":\"developed\"", third, StringComparison.Ordinal);
        Assert.Contains("\"windowSnapshotCount\":3", third, StringComparison.Ordinal);
        Assert.DoesNotContain("grid", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suggested", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissionEventCoordinator_BundlesSixSnapshotsAndStaysSilentWhenUnchanged()
    {
        MissionEventCoordinator coordinator = new();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-23T12:00:00Z");
        for (int sequence = 1; sequence <= 6; sequence++)
            Assert.Empty(coordinator.Observe(
                "mission-one",
                sequence,
                [],
                new Dictionary<string, string> { ["operations"] = "same" },
                now.AddSeconds(sequence * 4)));

        List<string> output = new();
        for (int sequence = 7; sequence <= 12; sequence++)
        {
            string signature = sequence < 9 ? "same" : "changed";
            output.AddRange(coordinator.Observe(
                "mission-one",
                sequence,
                [],
                new Dictionary<string, string> { ["operations"] = signature },
                now.AddSeconds(sequence * 4)));
        }

        string bundle = Assert.Single(output);
        Assert.Contains("\"eventType\":\"state-change-bundle\"", bundle, StringComparison.Ordinal);
        Assert.Contains("\"windowSnapshotCount\":6", bundle, StringComparison.Ordinal);
        Assert.Contains("\"operations\"", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void EventAssessment_MustMatchARecordedCandidate()
    {
        MemoryRepository repository = new();
        PlayerInformationJournal journal = new(repository);
        using JsonDocument assessment = JsonDocument.Parse("""
        {
          "eventAlias":"event-safe-one",
          "priority":"important",
          "outcome":"sitrep",
          "summary":"A developing contact requires a bounded situation report.",
          "confidence":"medium"
        }
        """);
        Assert.Throws<InvalidOperationException>(
            () => journal.ExecuteEventAssessment(assessment.RootElement));

        journal.RecordEventCandidate(
            "{\"eventAlias\":\"event-safe-one\",\"schema\":\"arma-ai-bridge/normalized-event-v2\"}");
        string result = journal.ExecuteEventAssessment(assessment.RootElement);

        Assert.Contains("\"priority\":\"important\"", result, StringComparison.Ordinal);
        Assert.Contains(
            repository.Entries,
            item => item.Provenance == "ai-event-assessment" &&
                    item.Tags.Contains("outcome:sitrep"));
    }

    [Fact]
    public void RuntimeContainsNoHiddenEnemyTruthOrMissionWideEnumeration()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string sqf = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    Path.Combine(root, "arma3", "addon-source"),
                    "*.sqf",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("allMissionObjects", sqf, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allUnits", sqf, StringComparison.OrdinalIgnoreCase);

        string assistant = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ArmaAiBridge.App",
            "Services",
            "OpenAiAssistantService.cs"));
        Assert.DoesNotContain("query_hidden_enemy", assistant, StringComparison.Ordinal);
        Assert.DoesNotContain("query_ground_truth", assistant, StringComparison.Ordinal);
    }

    private sealed class MemoryRepository : IMissionMemoryRepository
    {
        public string ActiveMissionKey => "test-mission";
        public List<MissionMemoryEntry> Entries { get; } = new();

        public long Remember(
            string text,
            string provenance,
            IReadOnlyList<string>? tags = null,
            WorldPosition? position = null)
        {
            long id = Entries.Count + 1;
            DateTimeOffset now = DateTimeOffset.Parse("2026-07-23T12:00:00Z");
            Entries.Add(new MissionMemoryEntry(
                id,
                text,
                provenance,
                now,
                now,
                tags?.ToArray() ?? Array.Empty<string>(),
                position));
            return id;
        }

        public IReadOnlyList<MissionMemoryEntry> SearchMemory(
            string query,
            int limit = 12,
            int maximumCharacters = 6000)
            => Entries.Take(limit).ToArray();

        public IReadOnlyList<MissionContactTrack> GetContactTracks(
            int limit = 256,
            bool includeForgotten = false) => Array.Empty<MissionContactTrack>();
        public IReadOnlyList<MissionContactObservation> GetContactObservations(
            string trackId,
            int limit = 20) => Array.Empty<MissionContactObservation>();
        public bool MarkContactDead(string trackId) => false;
        public bool ForgetContact(string trackId) => false;
        public bool UpdateMemory(
            long id,
            string text,
            IReadOnlyList<string>? tags = null,
            WorldPosition? position = null) => false;
        public bool ForgetMemory(long id) => false;
        public void SaveReportedLocation(ReportedLocationAnchor anchor) { }
        public ReportedLocationAnchor? GetReportedLocation(string key) => null;
        public IReadOnlyList<LoreSection> GetLoreSections() => Array.Empty<LoreSection>();
        public void SaveLoreSection(string scope, string content, bool enabled, bool alwaysInclude) { }
        public void ClearLoreSection(string scope) { }
    }
}
