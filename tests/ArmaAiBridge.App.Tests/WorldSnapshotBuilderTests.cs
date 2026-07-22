using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class WorldSnapshotBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CurrentSituation_RemovesPrivateAndOpaqueIdentityFields()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        WorldSnapshotBuilder snapshots = new(store, time);
        const string groupLabel = "SECRET GROUP LABEL";
        const string rawContactId = "SECRET OBJECT ID";
        ingest.Ingest(WorldModelTestData.Telemetry(
            group: groupLabel,
            contacts: new[] { WorldModelTestData.Contact(rawContactId) },
            sensorContacts: new[] { WorldModelTestData.Sensor(rawContactId) },
            vehicle: WorldModelTestData.Vehicle()));

        string json = snapshots.BuildCurrentSituation();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(WorldSnapshotBuilder.SnapshotSchema, root.GetProperty("schema").GetString());
        Assert.Equal("current-situation", root.GetProperty("purpose").GetString());
        Assert.Equal("player:self", root.GetProperty("player").GetProperty("entityId").GetString());
        Assert.Equal("group:self", root.GetProperty("group").GetProperty("entityId").GetString());
        JsonElement contact = root.GetProperty("knownContacts")[0];
        Assert.Equal("contact-001", contact.GetProperty("entityId").GetString());
        Assert.Equal("best-effort", contact.GetProperty("identityQuality").GetString());
        Assert.Equal("player", contact.GetProperty("source").GetString());
        Assert.Equal("live", contact.GetProperty("freshnessClass").GetString());
        Assert.Equal(96, contact.GetProperty("observedAtGameTime").GetDouble(), 3);
        Assert.Equal(18, contact.GetProperty("positionErrorMeters").GetDouble(), 3);
        Assert.True(contact.TryGetProperty("receivedAtUtc", out _));
        Assert.True(contact.TryGetProperty("confidence", out _));
        Assert.False(json.Contains("SECRET-PLAYER-UID", StringComparison.Ordinal));
        Assert.False(json.Contains("SECRET PLAYER NAME", StringComparison.Ordinal));
        Assert.False(json.Contains(groupLabel, StringComparison.Ordinal));
        Assert.False(json.Contains(rawContactId, StringComparison.Ordinal));
    }

    [Fact]
    public void PurposeSpecificSnapshots_SelectDifferentContext()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        WorldSnapshotBuilder snapshots = new(store, time);
        ingest.Ingest(WorldModelTestData.Telemetry(
            contacts: new[] { WorldModelTestData.Contact("contact") },
            vehicle: WorldModelTestData.Vehicle()));

        using JsonDocument current = JsonDocument.Parse(snapshots.BuildCurrentSituation());
        using JsonDocument contacts = JsonDocument.Parse(snapshots.BuildKnownContacts());

        Assert.True(current.RootElement.TryGetProperty("vehicle", out _));
        Assert.True(current.RootElement.GetProperty("player").TryGetProperty("weapon", out _));
        Assert.False(contacts.RootElement.TryGetProperty("vehicle", out _));
        Assert.False(contacts.RootElement.TryGetProperty("group", out _));
        Assert.True(contacts.RootElement.TryGetProperty("playerReference", out JsonElement reference));
        Assert.False(reference.TryGetProperty("weapon", out _));
        Assert.Equal("known-contacts", contacts.RootElement.GetProperty("purpose").GetString());
    }

    [Fact]
    public void FreshnessAndConfidenceDecayWithInjectedTimeAndHistoricalContactsAreOmitted()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        WorldSnapshotBuilder snapshots = new(store, time);
        ingest.Ingest(WorldModelTestData.Telemetry(
            contacts: new[] { WorldModelTestData.Contact("contact", lastSeenAge: 4) }));

        WorldKnownContactState live = Assert.Single(store.GetCurrentView().KnownContacts);
        Assert.Equal(WorldFreshness.Live, live.Metadata.FreshnessClass);
        Assert.Equal(0.95, live.Metadata.Confidence, 3);

        time.Advance(TimeSpan.FromSeconds(2));
        WorldKnownContactState recent = Assert.Single(store.GetCurrentView().KnownContacts);
        Assert.Equal(WorldFreshness.Recent, recent.Metadata.FreshnessClass);
        Assert.Equal(0.808, recent.Metadata.Confidence, 3);

        time.Advance(TimeSpan.FromSeconds(125));
        WorldKnownContactState historical = Assert.Single(store.GetCurrentView().KnownContacts);
        Assert.Equal(WorldFreshness.Historical, historical.Metadata.FreshnessClass);
        Assert.Equal(0.238, historical.Metadata.Confidence, 3);
        using JsonDocument snapshot = JsonDocument.Parse(snapshots.BuildCurrentSituation());
        Assert.Empty(snapshot.RootElement.GetProperty("knownContacts").EnumerateArray());
    }

    [Fact]
    public void UnknownContactAgeIsRepresentedConservativelyAndNotSentToOpenAi()
    {
        ManualTimeProvider time = new(Start);
        WorldStateStore store = new(time);
        using TelemetryIngestService ingest = new(store, time);
        WorldSnapshotBuilder snapshots = new(store, time);
        ingest.Ingest(WorldModelTestData.Telemetry(
            contacts: new[] { WorldModelTestData.UnknownAgeContact("unknown-age") }));

        WorldKnownContactState contact = Assert.Single(store.GetCurrentView().KnownContacts);
        Assert.Null(contact.LastSeenAgeSeconds);
        Assert.Equal(-1, contact.Metadata.ObservedAtGameTime);
        Assert.Equal(WorldFreshness.Historical, contact.Metadata.FreshnessClass);
        using JsonDocument snapshot = JsonDocument.Parse(snapshots.BuildKnownContacts());
        Assert.Empty(snapshot.RootElement.GetProperty("knownContacts").EnumerateArray());
    }

    [Fact]
    public void TryBuildReturnsFalseBeforeFirstTelemetry()
    {
        ManualTimeProvider time = new(Start);
        WorldSnapshotBuilder snapshots = new(new WorldStateStore(time), time);

        Assert.False(snapshots.TryBuildCurrentSituation(out string json));
        Assert.Equal(string.Empty, json);
    }
}
