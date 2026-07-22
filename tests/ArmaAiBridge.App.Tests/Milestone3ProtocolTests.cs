using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Milestone3ProtocolTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("session-handshake-v1")]
    [InlineData("friendly-force-snapshot-v1")]
    [InlineData("friendly-force-delta-v1")]
    [InlineData("mission-capabilities-v1")]
    public void ContractFixture_MatchesStrictTopLevelSchema(string contract)
    {
        using JsonDocument schema = JsonDocument.Parse(Fixture($"schemas/{contract}.schema.json"));
        using JsonDocument fixture = JsonDocument.Parse(Fixture($"{contract}.json"));
        JsonElement schemaRoot = schema.RootElement;
        JsonElement fixtureRoot = fixture.RootElement;

        Assert.False(schemaRoot.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            schemaRoot.GetProperty("properties").GetProperty("schema").GetProperty("const").GetString(),
            fixtureRoot.GetProperty("schema").GetString());
        foreach (JsonElement required in schemaRoot.GetProperty("required").EnumerateArray())
            Assert.True(fixtureRoot.TryGetProperty(required.GetString()!, out _), $"Missing {required} in {contract}.");
        foreach (JsonProperty property in fixtureRoot.EnumerateObject())
            Assert.True(schemaRoot.GetProperty("properties").TryGetProperty(property.Name, out _),
                $"Unexpected {property.Name} in {contract}.");
    }

    [Fact]
    public void SnapshotDeltaAndCapabilities_ReconcileWithStableAliasesAndNoRawIdentifiers()
    {
        (ManualTimeProvider time, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(Fixture("session-handshake-v1.json")).Status);
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(PlayerTelemetry()).Status);
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(Fixture("friendly-force-snapshot-v1.json")).Status);

            WorldStateView initial = store.GetCurrentView();
            Assert.Equal("own-side", initial.Protocol?.Visibility);
            Assert.Equal(4, initial.Protocol?.Features.Count);
            Assert.True(initial.Reconciliation.HasCompleteReconciliation);
            Assert.False(initial.Reconciliation.IsDegraded);
            Assert.Equal("group-001", Assert.Single(initial.FriendlyGroups).Alias);
            Assert.Equal(new[] { "unit-001", "unit-002" }, initial.FriendlyGroups[0].UnitAliases);
            Assert.Equal(new[] { "unit-002" }, Assert.Single(initial.FriendlyVehicles).CrewUnitAliases);
            Assert.Equal("asset-001", Assert.Single(initial.SupportAssets).Alias);

            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(Fixture("friendly-force-delta-v1.json")).Status);
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(Fixture("mission-capabilities-v1.json")).Status);
            WorldStateView reconciled = store.GetCurrentView();
            WorldFriendlyUnitState remaining = Assert.Single(reconciled.FriendlyUnits);
            Assert.Equal("unit-001", remaining.Alias);
            Assert.Equal(3500, remaining.Metadata.Position?.X);
            Assert.Equal(new[] { "unit-001" }, Assert.Single(reconciled.FriendlyGroups).UnitAliases);
            Assert.Empty(Assert.Single(reconciled.FriendlyVehicles).CrewUnitAliases);
            Assert.Equal("capability-001", Assert.Single(reconciled.Capabilities).Alias);
            Assert.Equal(1, reconciled.Reconciliation.CapabilityRegistryVersion);

            WorldSnapshotBuilder snapshots = new(store, time);
            string friendly = snapshots.BuildFriendlyForces(Arguments("""
                {"entityType":"all","maxDistanceMeters":50000,"includeStale":false,"limit":100}
                """));
            string assets = snapshots.BuildAssets(Arguments("""
                {"kind":"any","availableOnly":true,"maxDistanceMeters":50000,"includeStale":false,"limit":100}
                """));
            string capabilities = snapshots.BuildMissionCapabilities(Arguments(
                """{"enabledOnly":true,"includeStale":false}"""));

            foreach (string output in new[] { friendly, assets, capabilities })
            {
                Assert.DoesNotContain("source-session-a", output, StringComparison.Ordinal);
                Assert.DoesNotContain("m3-test-mission", output, StringComparison.Ordinal);
                Assert.DoesNotContain("net:2:", output, StringComparison.Ordinal);
                Assert.DoesNotContain("SECRET-PLAYER-UID", output, StringComparison.Ordinal);
                Assert.DoesNotContain("SECRET PLAYER NAME", output, StringComparison.Ordinal);
            }
            Assert.Contains("unit-001", friendly, StringComparison.Ordinal);
            Assert.DoesNotContain("unit-002", friendly, StringComparison.Ordinal);
            Assert.Contains("asset-001", assets, StringComparison.Ordinal);
            Assert.Contains("capability-001", capabilities, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SnapshotPages_AreAtomicAndMissingPagesExpireDeterministically()
    {
        (ManualTimeProvider time, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            ingest.Ingest(PlayerTelemetry());
            (string first, string second) = SplitSnapshotPages();

            TelemetryIngestResult buffered = ingest.Ingest(first);
            Assert.Equal("snapshot_page_buffered", buffered.DiagnosticCode);
            Assert.Empty(store.GetCurrentView().FriendlyUnits);
            Assert.Equal(1, store.GetCurrentView().Reconciliation.PendingPageCount);

            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(second).Status);
            Assert.Equal(2, store.GetCurrentView().FriendlyUnits.Count);
            Assert.True(store.GetCurrentView().Reconciliation.HasCompleteReconciliation);

            string nextHandshake = Fixture("session-handshake-v1.json")
                .Replace("source-session-a", "source-session-b", StringComparison.Ordinal)
                .Replace("message-000001", "message-session-b", StringComparison.Ordinal);
            Assert.True(ingest.Ingest(nextHandshake).SessionReset);
            Assert.Empty(store.GetCurrentView().FriendlyUnits);
            Assert.Equal("unit-001", UnitAliasAfterNewSession(ingest, store));

            string thirdHandshake = nextHandshake
                .Replace("source-session-b", "source-session-c", StringComparison.Ordinal)
                .Replace("message-session-b", "message-session-c", StringComparison.Ordinal);
            ingest.Ingest(thirdHandshake);
            ingest.Ingest(PlayerTelemetry("source-session-c"));
            (string incomplete, _) = SplitSnapshotPages("source-session-c");
            ingest.Ingest(incomplete);
            time.Advance(TimeSpan.FromSeconds(6));
            ingest.Ingest(PlayerTelemetry("source-session-c", timestamp: 20, frame: 2000));

            WorldReconciliationState expired = store.GetCurrentView().Reconciliation;
            Assert.True(expired.IsDegraded);
            Assert.Equal("incomplete_snapshot_expired", expired.DiagnosticCode);
            Assert.Equal(0, expired.PendingPageCount);
        }
    }

    [Fact]
    public void DuplicateIdentityAcrossSnapshotPages_IsRejectedAndMarksReconciliationDegraded()
    {
        (_, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            ingest.Ingest(PlayerTelemetry());
            (string firstJson, string secondJson) = SplitSnapshotPages();
            JsonNode first = JsonNode.Parse(firstJson)!;
            JsonNode second = JsonNode.Parse(secondJson)!;
            second["units"]![0]!["id"] = first["units"]![0]!["id"]!.GetValue<string>();

            Assert.Equal("snapshot_page_buffered", ingest.Ingest(first.ToJsonString()).DiagnosticCode);
            TelemetryIngestResult rejected = ingest.Ingest(second.ToJsonString());

            Assert.Equal(TelemetryIngestStatus.Rejected, rejected.Status);
            Assert.Equal("duplicate_unit_id_across_pages", rejected.DiagnosticCode);
            Assert.Empty(store.GetCurrentView().FriendlyUnits);
            Assert.True(store.GetCurrentView().Reconciliation.IsDegraded);
            Assert.Equal("duplicate_unit_id_across_pages", store.GetCurrentView().Reconciliation.DiagnosticCode);
        }
    }

    [Fact]
    public void FreshnessAndVisibility_AreConservativeAndCapabilityRegistryIsReadOnly()
    {
        (ManualTimeProvider time, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            ingest.Ingest(PlayerTelemetry());
            ingest.Ingest(Fixture("friendly-force-snapshot-v1.json"));
            ingest.Ingest(Fixture("friendly-force-delta-v1.json"));

            JsonNode capabilities = JsonNode.Parse(Fixture("mission-capabilities-v1.json"))!;
            JsonArray array = capabilities["capabilities"]!.AsArray();
            JsonNode eastOnly = array[0]!.DeepClone();
            eastOnly["id"] = "capability:east-only";
            eastOnly["constraints"]!["allowedRequesterSides"] = new JsonArray("EAST");
            array.Add(eastOnly);
            ingest.Ingest(capabilities.ToJsonString());
            Assert.Single(store.GetCurrentView().Capabilities);

            time.Advance(TimeSpan.FromSeconds(16));
            Assert.Equal(WorldFreshness.Stale, Assert.Single(store.GetCurrentView().FriendlyUnits).Metadata.FreshnessClass);
            WorldSnapshotBuilder snapshots = new(store, time);
            string freshOnly = snapshots.BuildFriendlyForces(Arguments("""
                {"entityType":"unit","maxDistanceMeters":50000,"includeStale":false,"limit":100}
                """));
            string withStale = snapshots.BuildFriendlyForces(Arguments("""
                {"entityType":"unit","maxDistanceMeters":50000,"includeStale":true,"limit":100}
                """));
            Assert.Empty(JsonDocument.Parse(freshOnly).RootElement.GetProperty("entities").EnumerateArray());
            Assert.Single(JsonDocument.Parse(withStale).RootElement.GetProperty("entities").EnumerateArray());

            string capabilityOutput = snapshots.BuildMissionCapabilities(Arguments(
                """{"enabledOnly":true,"includeStale":false}"""));
            Assert.DoesNotContain("east-only", capabilityOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("execute", capabilityOutput, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProtocolMismatchAndSequenceGap_AreReportedWithoutMixingSessions()
    {
        (_, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            string wrongSession = Fixture("friendly-force-snapshot-v1.json")
                .Replace("source-session-a", "source-session-other", StringComparison.Ordinal);
            Assert.Equal("protocol_session_mismatch", ingest.Ingest(wrongSession).DiagnosticCode);
            Assert.Empty(store.GetCurrentView().FriendlyUnits);

            JsonNode gap = JsonNode.Parse(Fixture("friendly-force-snapshot-v1.json"))!;
            gap["sequence"] = 4;
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(gap.ToJsonString()).Status);
            Assert.True(store.GetCurrentView().Reconciliation.HasCompleteReconciliation);
            Assert.False(store.GetCurrentView().Reconciliation.SequenceGap);

            JsonNode delta = JsonNode.Parse(Fixture("friendly-force-delta-v1.json"))!;
            delta["sequence"] = 6;
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(delta.ToJsonString()).Status);
            Assert.True(store.GetCurrentView().Reconciliation.SequenceGap);
            Assert.True(store.GetCurrentView().Reconciliation.IsDegraded);
            Assert.Equal(TelemetryIngestStatus.OutOfOrder, ingest.Ingest(delta.ToJsonString()).Status);
        }
    }

    [Fact]
    public void RuntimeValidator_EnforcesPublishedProtocolIdentifierLimit()
    {
        (_, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            JsonNode delta = JsonNode.Parse(Fixture("friendly-force-delta-v1.json"))!;
            delta["upsertUnits"]![0]!["id"] = new string('x', 129);

            TelemetryIngestResult result = ingest.Ingest(delta.ToJsonString());

            Assert.Equal(TelemetryIngestStatus.Rejected, result.Status);
            Assert.Equal("id_too_long", result.DiagnosticCode);
            Assert.Empty(store.GetCurrentView().FriendlyUnits);
            Assert.Equal(1, store.GetCurrentView().Reconciliation.LastSequence);
        }
    }

    [Fact]
    public void ExplicitSession_IsAuthoritativeOverLegacyResetHeuristicsAndHandshakeWorld()
    {
        (_, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            ingest.Ingest(PlayerTelemetry(timestamp: 100, frame: 1000));
            int sessionOrdinal = store.GetCurrentView().SessionOrdinal;

            TelemetryIngestResult regression = ingest.Ingest(PlayerTelemetry(timestamp: 10, frame: 1));
            Assert.Equal(TelemetryIngestStatus.OutOfOrder, regression.Status);
            Assert.False(regression.SessionReset);
            Assert.Equal(sessionOrdinal, store.GetCurrentView().SessionOrdinal);
            Assert.NotNull(store.GetCurrentView().Protocol);

            string wrongWorld = WorldModelTestData.Telemetry(
                timestamp: 101,
                frame: 1001,
                mapName: "Stratis",
                mapSize: 8192,
                missionId: "m3-test-mission",
                sessionId: "source-session-a");
            TelemetryIngestResult mismatch = ingest.Ingest(wrongWorld);
            Assert.Equal(TelemetryIngestStatus.Rejected, mismatch.Status);
            Assert.Equal("telemetry_world_mismatch", mismatch.DiagnosticCode);
            Assert.Equal("Altis", store.GetCurrentView().Map?.Name);
            Assert.NotNull(store.GetCurrentView().Protocol);

            JsonNode wrongHandshakeWorld = JsonNode.Parse(Fixture("session-handshake-v1.json"))!;
            wrongHandshakeWorld["sequence"] = 2;
            wrongHandshakeWorld["world"]!["name"] = "Stratis";
            wrongHandshakeWorld["world"]!["sizeMeters"] = 8192;
            Assert.Equal("handshake_world_mismatch", ingest.Ingest(wrongHandshakeWorld.ToJsonString()).DiagnosticCode);
        }
    }

    [Fact]
    public void HandshakeExpiryAndAuthorityChange_DegradeAndClearScopedState()
    {
        (ManualTimeProvider time, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            ingest.Ingest(PlayerTelemetry());
            ingest.Ingest(Fixture("friendly-force-snapshot-v1.json"));
            Assert.NotEmpty(store.GetCurrentView().FriendlyUnits);

            JsonNode narrowed = JsonNode.Parse(Fixture("session-handshake-v1.json"))!;
            narrowed["sequence"] = 3;
            narrowed["viewer"]!["visibility"] = "own-group";
            Assert.Equal(TelemetryIngestStatus.Applied, ingest.Ingest(narrowed.ToJsonString()).Status);
            Assert.Empty(store.GetCurrentView().FriendlyUnits);
            Assert.Empty(store.GetCurrentView().Capabilities);
            Assert.True(store.GetCurrentView().Reconciliation.IsDegraded);
            Assert.Equal("authority_changed", store.GetCurrentView().Reconciliation.DiagnosticCode);

            JsonNode refreshed = narrowed.DeepClone();
            refreshed["sequence"] = 4;
            ingest.Ingest(refreshed.ToJsonString());
            time.Advance(TimeSpan.FromSeconds(76));
            Assert.True(store.GetCurrentView().Reconciliation.IsDegraded);
            Assert.Equal("authority_changed", store.GetCurrentView().Reconciliation.DiagnosticCode);

            JsonNode newSession = JsonNode.Parse(Fixture("session-handshake-v1.json"))!;
            newSession["sessionId"] = "source-session-b";
            ingest.Ingest(newSession.ToJsonString());
            time.Advance(TimeSpan.FromSeconds(76));
            Assert.Equal("handshake_expired", store.GetCurrentView().Reconciliation.DiagnosticCode);
        }
    }

    [Fact]
    public void NewSession_DiscardsIncompleteReconciliationFromPreviousSession()
    {
        (ManualTimeProvider time, WorldStateStore store, TelemetryIngestService ingest) = CreateWorld();
        using (ingest)
        {
            ingest.Ingest(Fixture("session-handshake-v1.json"));
            (string incomplete, _) = SplitSnapshotPages();
            ingest.Ingest(incomplete);
            Assert.Equal(1, store.GetCurrentView().Reconciliation.PendingPageCount);

            string newHandshake = Fixture("session-handshake-v1.json")
                .Replace("source-session-a", "source-session-b", StringComparison.Ordinal);
            Assert.True(ingest.Ingest(newHandshake).SessionReset);
            Assert.Equal(0, store.GetCurrentView().Reconciliation.PendingPageCount);

            time.Advance(TimeSpan.FromSeconds(6));
            ingest.Ingest(PlayerTelemetry("source-session-b", timestamp: 20, frame: 2000));
            Assert.False(store.GetCurrentView().Reconciliation.IsDegraded);
            Assert.Equal(string.Empty, store.GetCurrentView().Reconciliation.DiagnosticCode);
        }
    }

    private static (ManualTimeProvider Time, WorldStateStore Store, TelemetryIngestService Ingest) CreateWorld()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        return (time, store, new TelemetryIngestService(store, time));
    }

    private static string PlayerTelemetry(string sessionId = "source-session-a", double timestamp = 10.5, long frame = 1000)
        => WorldModelTestData.Telemetry(
            timestamp: timestamp,
            frame: frame,
            missionId: "m3-test-mission",
            sessionId: sessionId,
            playerId: "net:2:31",
            groupId: "net:2:15");

    private static string UnitAliasAfterNewSession(TelemetryIngestService ingest, WorldStateStore store)
    {
        ingest.Ingest(PlayerTelemetry("source-session-b"));
        ingest.Ingest(Fixture("friendly-force-snapshot-v1.json")
            .Replace("source-session-a", "source-session-b", StringComparison.Ordinal));
        return store.GetCurrentView().FriendlyUnits[0].Alias;
    }

    private static (string First, string Second) SplitSnapshotPages(string sessionId = "source-session-a")
    {
        JsonNode first = JsonNode.Parse(Fixture("friendly-force-snapshot-v1.json"))!;
        first["sessionId"] = sessionId;
        first["pageCount"] = 2;
        JsonArray units = first["units"]!.AsArray();
        JsonNode secondUnit = units[1]!.DeepClone();
        units.RemoveAt(1);

        JsonNode second = first.DeepClone();
        second["messageId"] = "message-page-2";
        second["sequence"] = 3;
        second["pageIndex"] = 1;
        second["groups"] = new JsonArray();
        second["units"] = new JsonArray(secondUnit);
        second["vehicles"] = new JsonArray();
        second["assets"] = new JsonArray();
        return (first.ToJsonString(), second.ToJsonString());
    }

    private static JsonElement Arguments(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Fixture(string relativePath)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", relativePath)));
}
