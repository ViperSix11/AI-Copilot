using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class TelemetryIngestServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidTelemetry_BuildsProvenanceAwareEntitiesAndMergesSensorEvidence()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        const string rawContactId = "2:47";

        TelemetryIngestResult result = ingest.Ingest(WorldModelTestData.Telemetry(
            contacts: new[] { WorldModelTestData.Contact(rawContactId) },
            sensorContacts: new[] { WorldModelTestData.Sensor(rawContactId) },
            vehicle: WorldModelTestData.Vehicle()));
        WorldStateView view = store.GetCurrentView();

        Assert.Equal(TelemetryIngestStatus.Applied, result.Status);
        Assert.True(result.SessionReset);
        Assert.Equal(WorldResetReason.InitialTelemetry, result.ResetReason);
        Assert.Equal("session-0001", view.SessionId);
        Assert.Equal("player:self", view.Player!.Metadata.EntityId);
        Assert.Equal(EntityIdentityQuality.Stable, view.Player.Metadata.IdentityQuality);
        Assert.Equal(EntityIdentityQuality.BestEffort, view.Group!.Metadata.IdentityQuality);
        Assert.Equal(EntityIdentityQuality.Slot, view.Vehicle!.Metadata.IdentityQuality);

        WorldKnownContactState contact = Assert.Single(view.KnownContacts);
        Assert.Equal("contact-001", contact.Alias);
        Assert.Equal(EntityIdentityQuality.BestEffort, contact.Metadata.IdentityQuality);
        Assert.Equal(WorldProvenance.Player, contact.Metadata.Source);
        Assert.Equal(
            new[] { WorldProvenance.Player, WorldProvenance.Group, WorldProvenance.Sensor },
            contact.Metadata.EvidenceSources);
        Assert.Equal(96, contact.Metadata.ObservedAtGameTime, 3);
        Assert.Equal(Start, contact.Metadata.ReceivedAtUtc);
        Assert.Equal(4, contact.Metadata.AgeSeconds, 3);
        Assert.Equal(WorldFreshness.Live, contact.Metadata.FreshnessClass);
        Assert.Equal(0.95, contact.Metadata.Confidence, 3);
        Assert.Equal(18, contact.Metadata.PositionErrorMeters);
        Assert.DoesNotContain(rawContactId, contact.Alias, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedAndOmittedContacts_KeepAliasAndAgeLastKnownEvidence()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        object contact = WorldModelTestData.Contact("opaque-contact", lastSeenAge: 4);

        ingest.Ingest(WorldModelTestData.Telemetry(contacts: new[] { contact }));
        time.Advance(TimeSpan.FromMilliseconds(250));
        ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 100.25, frame: 1001, contacts: new[] { contact }));
        time.Advance(TimeSpan.FromMilliseconds(750));
        TelemetryIngestResult omitted = ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 101, frame: 1002, contacts: Array.Empty<object>()));

        WorldKnownContactState retained = Assert.Single(store.GetCurrentView().KnownContacts);
        Assert.Equal(TelemetryIngestStatus.Applied, omitted.Status);
        Assert.Equal(1, omitted.KnownContactCount);
        Assert.Equal("contact-001", retained.Alias);
        Assert.Equal(5, retained.Metadata.AgeSeconds, 3);
        Assert.Equal(5, retained.LastSeenAgeSeconds!.Value, 3);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(WorldFreshness.Recent,
            Assert.Single(store.GetCurrentView().KnownContacts).Metadata.FreshnessClass);
    }

    [Fact]
    public void RepeatedCachedContact_DoesNotEmitAFalseContactDelta()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        List<WorldStateDelta> deltas = new();
        store.StateChanged += deltas.Add;
        object contact = WorldModelTestData.Contact("opaque-contact", lastSeenAge: 4);

        ingest.Ingest(WorldModelTestData.Telemetry(contacts: new[] { contact }));
        ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 100.25, frame: 1001, contacts: new[] { contact }));

        Assert.Contains("contact-001", deltas[0].UpdatedEntityIds);
        Assert.DoesNotContain("contact-001", deltas[1].UpdatedEntityIds);
    }

    [Fact]
    public void MapTimeAndFrameRegressions_StartNewSessionsAndClearAliases()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);

        ingest.Ingest(WorldModelTestData.Telemetry(
            contacts: new[] { WorldModelTestData.Contact("old") }));
        TelemetryIngestResult mapReset = ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 101, frame: 1001, mapName: "Stratis", mapSize: 8192,
            contacts: new[] { WorldModelTestData.Contact("map-new") }));
        Assert.True(mapReset.SessionReset);
        Assert.Equal(WorldResetReason.MapChanged, mapReset.ResetReason);
        Assert.Equal("session-0002", store.GetCurrentView().SessionId);
        Assert.Equal("contact-001", Assert.Single(store.GetCurrentView().KnownContacts).Alias);

        TelemetryIngestResult timeReset = ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 1, frame: 1002, mapName: "Stratis", mapSize: 8192,
            contacts: new[] { WorldModelTestData.Contact("time-new") }));
        Assert.Equal(WorldResetReason.MissionTimeRegressed, timeReset.ResetReason);
        Assert.Equal("session-0003", store.GetCurrentView().SessionId);

        ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 2, frame: 2000, mapName: "Stratis", mapSize: 8192));
        TelemetryIngestResult frameReset = ingest.Ingest(WorldModelTestData.Telemetry(
            timestamp: 3, frame: 1900, mapName: "Stratis", mapSize: 8192));
        Assert.Equal(WorldResetReason.FrameRegressed, frameReset.ResetReason);
        Assert.Equal("session-0004", store.GetCurrentView().SessionId);
        Assert.Empty(store.GetCurrentView().KnownContacts);
    }

    [Fact]
    public void SlightlyOutOfOrderObservation_IsIgnoredWithoutReset()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        ingest.Ingest(WorldModelTestData.Telemetry(timestamp: 100, frame: 1000));

        TelemetryIngestResult result = ingest.Ingest(
            WorldModelTestData.Telemetry(timestamp: 99.75, frame: 999));

        Assert.Equal(TelemetryIngestStatus.OutOfOrder, result.Status);
        Assert.Equal(1, store.GetCurrentView().SessionOrdinal);
        Assert.Equal(100, store.GetCurrentView().LastObservedAtGameTime);
    }

    [Fact]
    public void InvalidAndUnrelatedMessages_DoNotCreateWorldState()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);

        Assert.Equal(TelemetryIngestStatus.Rejected, ingest.Ingest("{not json").Status);
        Assert.Equal(TelemetryIngestStatus.Ignored, ingest.Ingest(
            "{\"schema\":\"arma-ai-bridge/arma3/query-result-v1\"}").Status);
        TelemetryIngestResult invalidTelemetry = ingest.Ingest(
            "{\"schema\":\"arma-ai-bridge/arma3/telemetry-v1\",\"timestamp\":1}");

        Assert.Equal(TelemetryIngestStatus.Rejected, invalidTelemetry.Status);
        Assert.False(store.GetCurrentView().HasTelemetry);
    }

    [Fact]
    public void BestEffortGroupIdentityIsStableUntilItsOnlyAvailableIdentityFieldsChange()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);

        ingest.Ingest(WorldModelTestData.Telemetry(group: "Alpha 1-1"));
        string first = store.GetCurrentView().Group!.Metadata.EntityId;
        ingest.Ingest(WorldModelTestData.Telemetry(timestamp: 101, frame: 1001, group: "Alpha 1-1"));
        string repeated = store.GetCurrentView().Group!.Metadata.EntityId;
        ingest.Ingest(WorldModelTestData.Telemetry(timestamp: 102, frame: 1002, group: "Bravo 1-1"));
        string renamed = store.GetCurrentView().Group!.Metadata.EntityId;

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, renamed);
        Assert.Equal("player:self", store.GetCurrentView().Player!.Metadata.EntityId);
    }

    [Fact]
    public void AssignedVehicleRoleArray_IsNormalizedWithoutChangingTheProtocol()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);

        TelemetryIngestResult result = ingest.Ingest(WorldModelTestData.Telemetry(
            vehicle: WorldModelTestData.Vehicle(new object[] { "Turret", new[] { 0, 1 } })));

        Assert.Equal(TelemetryIngestStatus.Applied, result.Status);
        Assert.Equal("Turret [0,1]", store.GetCurrentView().Vehicle!.Role);
    }

    [Fact]
    public void SensorOnlyContact_HasSensorProvenanceWithoutInventedGeometry()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);

        ingest.Ingest(WorldModelTestData.Telemetry(
            sensorContacts: new[] { WorldModelTestData.Sensor("sensor-only") }));

        WorldKnownContactState contact = Assert.Single(store.GetCurrentView().KnownContacts);
        Assert.Equal(WorldProvenance.Sensor, contact.Metadata.Source);
        Assert.Equal(new[] { WorldProvenance.Sensor }, contact.Metadata.EvidenceSources);
        Assert.Null(contact.Metadata.Position);
        Assert.Null(contact.Metadata.PositionErrorMeters);
        Assert.Equal(WorldFreshness.Live, contact.Metadata.FreshnessClass);
        Assert.Equal("contact-001", contact.Alias);
    }
}
