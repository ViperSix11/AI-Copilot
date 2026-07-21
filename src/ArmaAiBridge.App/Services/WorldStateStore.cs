using System.Security.Cryptography;
using System.Text;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class WorldStateStore
{
    private const double OrderingToleranceSeconds = 0.5;
    private const long MaterialFrameRegression = 60;
    private const double UnknownContactAgeSeconds = 120.001;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ContactEntry> _contacts = new(StringComparer.Ordinal);
    private bool _isConnected;
    private long _version;
    private int _sessionOrdinal;
    private int _nextContactAlias = 1;
    private string _sessionId = string.Empty;
    private WorldResetReason? _lastResetReason;
    private DateTimeOffset? _sessionStartedAtUtc;
    private DateTimeOffset? _lastReceivedAtUtc;
    private double _lastGameTime;
    private long _lastFrame = -1;
    private MapEntry? _map;
    private PlayerEntry? _player;
    private GroupEntry? _group;
    private VehicleEntry? _vehicle;

    public WorldStateStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<WorldStateDelta>? StateChanged;

    public WorldStateView GetCurrentView()
    {
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            return new WorldStateView(
                _version,
                _isConnected,
                _sessionOrdinal > 0,
                _sessionId,
                _sessionOrdinal,
                _lastResetReason,
                _sessionStartedAtUtc,
                _lastReceivedAtUtc,
                _lastGameTime,
                _lastFrame,
                BuildMap(now),
                BuildPlayer(now),
                BuildGroup(now),
                BuildVehicle(now),
                _contacts.Values
                    .OrderBy(contact => contact.Alias, StringComparer.Ordinal)
                    .Select(contact => BuildContact(contact, now))
                    .ToArray());
        }
    }

    public void SetConnected(bool connected)
    {
        WorldStateDelta? delta = null;
        lock (_gate)
        {
            if (_isConnected != connected)
            {
                _isConnected = connected;
                _version++;
                delta = new WorldStateDelta(
                    _version, ConnectionChanged: true, SessionReset: false, ResetReason: null,
                    UpdatedEntityIds: Array.Empty<string>());
            }
        }
        if (delta is not null) PublishStateChanged(delta);
    }

    internal TelemetryIngestResult Apply(TelemetryObservation observation)
    {
        TelemetryIngestResult result;
        WorldStateDelta delta;
        lock (_gate)
        {
            WorldResetReason? resetReason = DetectReset(observation);
            if (resetReason is null && IsOutOfOrder(observation))
            {
                return new TelemetryIngestResult(
                    TelemetryIngestStatus.OutOfOrder,
                    KnownContactCount: _contacts.Count,
                    SkippedEntityCount: observation.SkippedEntityCount,
                    DiagnosticCode: "out_of_order");
            }

            if (resetReason is not null)
            {
                StartSession(resetReason.Value, observation.ReceivedAtUtc);
            }

            List<string> updatedEntityIds = ApplyCurrentState(observation);
            updatedEntityIds.AddRange(ApplyContacts(observation));
            _lastGameTime = observation.GameTime;
            _lastFrame = observation.Frame;
            _lastReceivedAtUtc = observation.ReceivedAtUtc;
            _version++;

            result = new TelemetryIngestResult(
                TelemetryIngestStatus.Applied,
                resetReason is not null,
                resetReason,
                _contacts.Count,
                observation.SkippedEntityCount);
            delta = new WorldStateDelta(
                _version,
                ConnectionChanged: false,
                SessionReset: resetReason is not null,
                ResetReason: resetReason,
                UpdatedEntityIds: updatedEntityIds.Distinct(StringComparer.Ordinal).ToArray());
        }
        PublishStateChanged(delta);
        return result;
    }

    private WorldResetReason? DetectReset(TelemetryObservation observation)
    {
        if (_sessionOrdinal == 0) return WorldResetReason.InitialTelemetry;
        if (_map is not null &&
            (!string.Equals(_map.Name, observation.Map.Name, StringComparison.OrdinalIgnoreCase) ||
             Math.Abs(_map.SizeMeters - observation.Map.SizeMeters) > 0.5))
        {
            return WorldResetReason.MapChanged;
        }
        if (observation.GameTime < _lastGameTime - OrderingToleranceSeconds)
            return WorldResetReason.MissionTimeRegressed;
        if (observation.Frame >= 0 && _lastFrame >= 0 &&
            observation.Frame < _lastFrame - MaterialFrameRegression)
        {
            return WorldResetReason.FrameRegressed;
        }
        return null;
    }

    private bool IsOutOfOrder(TelemetryObservation observation)
    {
        if (observation.GameTime < _lastGameTime) return true;
        return observation.GameTime == _lastGameTime &&
               observation.Frame >= 0 && _lastFrame >= 0 && observation.Frame <= _lastFrame;
    }

    private void StartSession(WorldResetReason reason, DateTimeOffset receivedAtUtc)
    {
        _sessionOrdinal++;
        _sessionId = $"session-{_sessionOrdinal:0000}";
        _lastResetReason = reason;
        _sessionStartedAtUtc = receivedAtUtc;
        _lastReceivedAtUtc = null;
        _lastGameTime = 0;
        _lastFrame = -1;
        _map = null;
        _player = null;
        _group = null;
        _vehicle = null;
        _contacts.Clear();
        _nextContactAlias = 1;
    }

    private List<string> ApplyCurrentState(TelemetryObservation observation)
    {
        bool vehicleChanged = _vehicle is not null || observation.Vehicle is not null;
        ObservationStamp stamp = new(observation.GameTime, observation.ReceivedAtUtc, 0);
        _map = new MapEntry(observation.Map.Name, observation.Map.SizeMeters, observation.Map.Grid,
            observation.Map.Daytime, stamp);
        _player = new PlayerEntry(observation.Player, stamp);

        string groupIdentityInput = $"{observation.Player.Side}\0{observation.Player.GroupLabel}";
        string groupId = "group:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(groupIdentityInput)))[..12].ToLowerInvariant();
        _group = new GroupEntry(groupId, observation.Player.Side, observation.Player.GroupLabel, stamp);
        _vehicle = observation.Vehicle is null ? null : new VehicleEntry(observation.Vehicle, stamp);
        List<string> updated = new() { "map:current", "player:self", groupId };
        if (vehicleChanged) updated.Add("vehicle:current");
        return updated;
    }

    private List<string> ApplyContacts(TelemetryObservation observation)
    {
        List<string> updated = new();
        foreach (ContactObservation contact in observation.Contacts)
        {
            ContactEntry entry = GetOrCreateContact(contact.OpaqueId);
            if (!string.Equals(entry.KnownSignature, contact.Signature, StringComparison.Ordinal))
            {
                double sourceAge = contact.LastSeenAgeSeconds is >= 0
                    ? contact.LastSeenAgeSeconds.Value
                    : UnknownContactAgeSeconds;
                entry.Known = contact;
                entry.KnownSignature = contact.Signature;
                entry.KnownStamp = new ObservationStamp(
                    contact.LastSeenAgeSeconds is >= 0
                        ? Math.Max(0, observation.GameTime - sourceAge)
                        : -1,
                    observation.ReceivedAtUtc,
                    sourceAge);
                entry.ThreatAgeAtReceive = contact.LastThreatAgeSeconds is >= 0
                    ? contact.LastThreatAgeSeconds
                    : null;
                updated.Add(entry.Alias);
            }
        }

        foreach (SensorContactObservation sensor in observation.SensorContacts)
        {
            ContactEntry entry = GetOrCreateContact(sensor.OpaqueId);
            entry.Sensor = sensor;
            entry.SensorStamp = new ObservationStamp(observation.GameTime, observation.ReceivedAtUtc, 0);
            updated.Add(entry.Alias);
        }
        return updated;
    }

    private ContactEntry GetOrCreateContact(string opaqueId)
    {
        if (_contacts.TryGetValue(opaqueId, out ContactEntry? existing)) return existing;
        ContactEntry created = new($"contact-{_nextContactAlias++:000}");
        _contacts.Add(opaqueId, created);
        return created;
    }

    private WorldMapState? BuildMap(DateTimeOffset now)
    {
        if (_map is null) return null;
        return new WorldMapState(
            Metadata("map:current", EntityIdentityQuality.Stable, WorldProvenance.Mission,
                new[] { WorldProvenance.Mission }, _map.Stamp, now, 1, null, null, contact: false),
            _map.Name, _map.SizeMeters, _map.Grid, _map.Daytime);
    }

    private WorldPlayerState? BuildPlayer(DateTimeOffset now)
    {
        if (_player is null) return null;
        PlayerObservation value = _player.Value;
        return new WorldPlayerState(
            Metadata("player:self", EntityIdentityQuality.Stable, WorldProvenance.Player,
                new[] { WorldProvenance.Player }, _player.Stamp, now, 1, value.PositionAtl, null, contact: false),
            value.Side, value.PositionAsl, value.BodyHeading, value.ViewHeading, value.SpeedKph,
            value.Damage, value.LifeState, value.Stance, value.Weapon, value.Magazine, value.Muzzle,
            value.LoadedRounds, value.MatchingMagazineCount, value.MatchingMagazineRounds);
    }

    private WorldGroupState? BuildGroup(DateTimeOffset now)
    {
        if (_group is null) return null;
        return new WorldGroupState(
            Metadata(_group.EntityId, EntityIdentityQuality.BestEffort, WorldProvenance.Group,
                new[] { WorldProvenance.Group }, _group.Stamp, now, 0.9, null, null, contact: false),
            _group.Side, _group.LocalLabel);
    }

    private WorldVehicleState? BuildVehicle(DateTimeOffset now)
    {
        if (_vehicle is null) return null;
        VehicleObservation value = _vehicle.Value;
        return new WorldVehicleState(
            Metadata("vehicle:current", EntityIdentityQuality.Slot, WorldProvenance.Player,
                new[] { WorldProvenance.Player }, _vehicle.Stamp, now, 0.95,
                value.PositionAtl, null, contact: false),
            value.Class, value.DisplayName, value.Heading, value.SpeedKph, value.Fuel, value.Damage, value.Role);
    }

    private static WorldKnownContactState BuildContact(ContactEntry entry, DateTimeOffset now)
    {
        ContactObservation? known = entry.Known;
        SensorContactObservation? sensor = entry.Sensor;
        ObservationStamp stamp = known is not null && entry.KnownStamp is not null
            ? entry.KnownStamp.Value
            : entry.SensorStamp ?? default;

        List<WorldProvenance> evidence = new();
        if (known?.KnownByPlayer == true) evidence.Add(WorldProvenance.Player);
        if (known?.KnownByGroup == true) evidence.Add(WorldProvenance.Group);
        if (sensor is not null) evidence.Add(WorldProvenance.Sensor);
        if (evidence.Count == 0) evidence.Add(WorldProvenance.Derived);

        WorldProvenance source = known?.KnownByPlayer == true ? WorldProvenance.Player
            : known?.KnownByGroup == true ? WorldProvenance.Group
            : sensor is not null ? WorldProvenance.Sensor
            : WorldProvenance.Derived;
        double baseConfidence = known?.KnownByPlayer == true && known.KnownByGroup ? 0.95
            : known?.KnownByPlayer == true ? 0.9
            : known?.KnownByGroup == true ? 0.78
            : sensor is not null ? 0.7
            : 0.5;
        double elapsed = Math.Max(0, (now - stamp.ReceivedAtUtc).TotalSeconds);

        return new WorldKnownContactState(
            Metadata(entry.Alias, EntityIdentityQuality.BestEffort, source, evidence, stamp, now,
                baseConfidence, known?.EstimatedPosition, known?.PositionErrorMeters, contact: true),
            entry.Alias,
            FirstNonEmpty(known?.Class, sensor?.Class),
            known?.DisplayName ?? string.Empty,
            known?.KnownByPlayer == true,
            known?.KnownByGroup == true,
            known?.LastSeenAgeSeconds is null ? null : stamp.SourceAgeAtReceive + elapsed,
            known is null || entry.ThreatAgeAtReceive is null ? null : entry.ThreatAgeAtReceive + elapsed,
            known?.PerceivedSide ?? string.Empty,
            known?.Ignored == true,
            sensor?.TargetType ?? string.Empty,
            sensor?.Relationship ?? string.Empty,
            sensor?.Sensors ?? Array.Empty<string>());
    }

    private static string FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;

    private static WorldEntityMetadata Metadata(
        string entityId,
        EntityIdentityQuality identityQuality,
        WorldProvenance source,
        IReadOnlyList<WorldProvenance> evidenceSources,
        ObservationStamp stamp,
        DateTimeOffset now,
        double baseConfidence,
        WorldPosition? position,
        double? positionErrorMeters,
        bool contact)
    {
        double age = Math.Max(0, stamp.SourceAgeAtReceive + (now - stamp.ReceivedAtUtc).TotalSeconds);
        WorldFreshness freshness = ClassifyFreshness(age, contact);
        double freshnessFactor = freshness switch
        {
            WorldFreshness.Live => 1,
            WorldFreshness.Recent => 0.85,
            WorldFreshness.Stale => 0.6,
            _ => 0.25
        };
        return new WorldEntityMetadata(
            entityId,
            identityQuality,
            source,
            evidenceSources,
            stamp.ObservedAtGameTime,
            stamp.ReceivedAtUtc,
            Math.Round(age, 3),
            freshness,
            Math.Round(baseConfidence * freshnessFactor, 3),
            position,
            positionErrorMeters);
    }

    private static WorldFreshness ClassifyFreshness(double ageSeconds, bool contact)
    {
        if (contact)
        {
            if (ageSeconds <= 5) return WorldFreshness.Live;
            if (ageSeconds <= 30) return WorldFreshness.Recent;
            if (ageSeconds <= 120) return WorldFreshness.Stale;
            return WorldFreshness.Historical;
        }
        if (ageSeconds <= 1) return WorldFreshness.Live;
        if (ageSeconds <= 5) return WorldFreshness.Recent;
        if (ageSeconds <= 30) return WorldFreshness.Stale;
        return WorldFreshness.Historical;
    }

    private void PublishStateChanged(WorldStateDelta delta)
    {
        Delegate[] subscribers = StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (Action<WorldStateDelta> subscriber in subscribers.Cast<Action<WorldStateDelta>>())
        {
            try { subscriber(delta); }
            catch { }
        }
    }

    private readonly record struct ObservationStamp(
        double ObservedAtGameTime,
        DateTimeOffset ReceivedAtUtc,
        double SourceAgeAtReceive);

    private sealed record MapEntry(
        string Name, double SizeMeters, string Grid, double Daytime, ObservationStamp Stamp);

    private sealed record PlayerEntry(PlayerObservation Value, ObservationStamp Stamp);

    private sealed record GroupEntry(
        string EntityId, string Side, string LocalLabel, ObservationStamp Stamp);

    private sealed record VehicleEntry(VehicleObservation Value, ObservationStamp Stamp);

    private sealed class ContactEntry
    {
        public ContactEntry(string alias) => Alias = alias;
        public string Alias { get; }
        public ContactObservation? Known { get; set; }
        public string KnownSignature { get; set; } = string.Empty;
        public ObservationStamp? KnownStamp { get; set; }
        public double? ThreatAgeAtReceive { get; set; }
        public SensorContactObservation? Sensor { get; set; }
        public ObservationStamp? SensorStamp { get; set; }
    }
}
