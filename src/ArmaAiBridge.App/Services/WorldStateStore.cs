using System.Security.Cryptography;
using System.Text;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class WorldStateStore
{
    private const double OrderingToleranceSeconds = 0.5;
    private const long MaterialFrameRegression = 60;
    private const double UnknownContactAgeSeconds = 120.001;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(75);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ContactEntry> _contacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FriendlyGroupEntry> _friendlyGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FriendlyUnitEntry> _friendlyUnits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FriendlyVehicleEntry> _friendlyVehicles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SupportAssetEntry> _supportAssets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityEntry> _capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _groupAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unitAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _vehicleAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _assetAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capabilityAliases = new(StringComparer.Ordinal);
    private bool _isConnected;
    private long _version;
    private int _sessionOrdinal;
    private int _nextContactAlias = 1;
    private int _nextGroupAlias = 1;
    private int _nextUnitAlias = 1;
    private int _nextVehicleAlias = 1;
    private int _nextAssetAlias = 1;
    private int _nextCapabilityAlias = 1;
    private string _sessionId = string.Empty;
    private string _sourceSessionId = string.Empty;
    private string _sourceMissionId = string.Empty;
    private WorldResetReason? _lastResetReason;
    private DateTimeOffset? _sessionStartedAtUtc;
    private DateTimeOffset? _lastReceivedAtUtc;
    private double _lastGameTime;
    private long _lastFrame = -1;
    private MapEntry? _map;
    private PlayerEntry? _player;
    private GroupEntry? _group;
    private VehicleEntry? _vehicle;
    private ProtocolEntry? _protocol;
    private long _lastProtocolSequence;
    private bool _sequenceGap;
    private int _pendingPageCount;
    private bool _hasCompleteReconciliation;
    private string _lastReconciliationId = string.Empty;
    private DateTimeOffset? _lastReconciledAtUtc;
    private string _reconciliationDiagnostic = string.Empty;
    private int _capabilityRegistryVersion;

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
            bool handshakeExpired = _protocol is not null && now - _protocol.ReceivedAtUtc > HandshakeTimeout;
            string reconciliationDiagnostic = _reconciliationDiagnostic.Length > 0
                ? _reconciliationDiagnostic
                : handshakeExpired ? "handshake_expired" : string.Empty;
            return new WorldStateView(
                _version,
                _isConnected,
                _player is not null,
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
                    .ToArray(),
                BuildProtocol(),
                _friendlyGroups.Values.OrderBy(item => item.Alias, StringComparer.Ordinal)
                    .Select(item => BuildFriendlyGroup(item, now)).ToArray(),
                _friendlyUnits.Values.OrderBy(item => item.Alias, StringComparer.Ordinal)
                    .Select(item => BuildFriendlyUnit(item, now)).ToArray(),
                _friendlyVehicles.Values.OrderBy(item => item.Alias, StringComparer.Ordinal)
                    .Select(item => BuildFriendlyVehicle(item, now)).ToArray(),
                _supportAssets.Values.OrderBy(item => item.Alias, StringComparer.Ordinal)
                    .Select(item => BuildSupportAsset(item, now)).ToArray(),
                _capabilities.Values.OrderBy(item => item.Alias, StringComparer.Ordinal)
                    .Select(item => BuildCapability(item, now)).ToArray(),
                new WorldReconciliationState(
                    _protocol is not null,
                    _hasCompleteReconciliation,
                    _lastReconciliationId,
                    _lastReconciledAtUtc,
                    _lastProtocolSequence,
                    _sequenceGap,
                    _pendingPageCount,
                    _sequenceGap || _pendingPageCount > 0 || reconciliationDiagnostic.Length > 0,
                    reconciliationDiagnostic,
                    _capabilityRegistryVersion));
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

    internal TelemetryIngestResult ApplyHandshake(SessionHandshakeObservation observation)
    {
        WorldStateDelta delta;
        bool reset;
        lock (_gate)
        {
            reset = _sourceSessionId.Length == 0 ||
                    !string.Equals(_sourceSessionId, observation.Envelope.SessionId, StringComparison.Ordinal);
            if (reset)
            {
                StartSession(WorldResetReason.ProtocolSessionChanged, observation.Envelope.ReceivedAtUtc);
                _sourceSessionId = observation.Envelope.SessionId;
                _sourceMissionId = observation.Envelope.MissionId;
            }
            else if (!string.Equals(_sourceMissionId, observation.Envelope.MissionId, StringComparison.Ordinal))
            {
                return Rejected("handshake_mission_mismatch");
            }
            else if (_protocol is not null &&
                     (!string.Equals(_protocol.WorldName, observation.WorldName, StringComparison.OrdinalIgnoreCase) ||
                      Math.Abs(_protocol.WorldSizeMeters - observation.WorldSizeMeters) > 0.5))
            {
                return Rejected("handshake_world_mismatch");
            }

            TelemetryIngestResult sequence = AcceptProtocolSequence(observation.Envelope);
            if (sequence.Status != TelemetryIngestStatus.Applied) return sequence;
            bool authorityChanged = !reset && _protocol is not null &&
                (!string.Equals(_protocol.ViewerSide, observation.ViewerSide, StringComparison.Ordinal) ||
                 !string.Equals(_protocol.Visibility, observation.Visibility, StringComparison.Ordinal));
            if (authorityChanged) ClearAuthorityScopedState();
            _protocol = new ProtocolEntry(
                observation.ProtocolMajor,
                observation.ProtocolMinor,
                observation.WorldName,
                observation.WorldSizeMeters,
                observation.ViewerSide,
                observation.Visibility,
                observation.Envelope.ReceivedAtUtc,
                observation.Features);
            _lastReceivedAtUtc = observation.Envelope.ReceivedAtUtc;
            _version++;
            delta = new WorldStateDelta(
                _version, ConnectionChanged: false, SessionReset: reset,
                ResetReason: reset ? WorldResetReason.ProtocolSessionChanged : null,
                UpdatedEntityIds: authorityChanged
                    ? new[] { "protocol:session", "protocol:authority", "reconciliation:pending" }
                    : new[] { "protocol:session" });
        }
        PublishStateChanged(delta);
        return new TelemetryIngestResult(
            TelemetryIngestStatus.Applied, reset, reset ? WorldResetReason.ProtocolSessionChanged : null);
    }

    internal TelemetryIngestResult AcceptProtocolEvent(ProtocolEnvelope envelope)
    {
        WorldStateDelta delta;
        lock (_gate)
        {
            if (_sourceSessionId.Length == 0 ||
                !string.Equals(_sourceSessionId, envelope.SessionId, StringComparison.Ordinal) ||
                !string.Equals(_sourceMissionId, envelope.MissionId, StringComparison.Ordinal))
            {
                return Rejected("protocol_session_mismatch");
            }
            TelemetryIngestResult result = AcceptProtocolSequence(envelope);
            if (result.Status != TelemetryIngestStatus.Applied) return result;
            _lastReceivedAtUtc = envelope.ReceivedAtUtc;
            _version++;
            delta = new WorldStateDelta(
                _version, ConnectionChanged: false, SessionReset: false, ResetReason: null,
                UpdatedEntityIds: new[] { "protocol:sequence" });
        }
        PublishStateChanged(delta);
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
    }

    internal TelemetryIngestResult ApplyFriendlySnapshot(FriendlyForceSnapshotObservation observation)
    {
        WorldStateDelta delta;
        lock (_gate)
        {
            string viewerSide = _protocol?.ViewerSide ?? string.Empty;
            ObservationStamp stamp = new(observation.Envelope.GameTime, observation.Envelope.ReceivedAtUtc, 0);

            Dictionary<string, FriendlyGroupEntry> groups = observation.Groups
                .Where(item => viewerSide.Length == 0 || item.Side == viewerSide)
                .ToDictionary(item => item.SourceId, item => new FriendlyGroupEntry(
                    GetAlias(_groupAliases, ref _nextGroupAlias, "group", item.SourceId), item, stamp, 1),
                    StringComparer.Ordinal);
            Dictionary<string, FriendlyUnitEntry> units = observation.Units
                .Where(item => viewerSide.Length == 0 || item.Side == viewerSide)
                .ToDictionary(item => item.SourceId, item => new FriendlyUnitEntry(
                    GetAlias(_unitAliases, ref _nextUnitAlias, "unit", item.SourceId), item, stamp, 1),
                    StringComparer.Ordinal);
            Dictionary<string, FriendlyVehicleEntry> vehicles = observation.Vehicles
                .Where(item => viewerSide.Length == 0 || item.Side == viewerSide)
                .ToDictionary(item => item.SourceId, item => new FriendlyVehicleEntry(
                    GetAlias(_vehicleAliases, ref _nextVehicleAlias, "vehicle", item.SourceId), item, stamp, 1),
                    StringComparer.Ordinal);
            Dictionary<string, SupportAssetEntry> assets = observation.Assets
                .ToDictionary(item => item.SourceId, item => new SupportAssetEntry(
                    GetAlias(_assetAliases, ref _nextAssetAlias, "asset", item.SourceId), item, stamp, 1),
                    StringComparer.Ordinal);

            Replace(_friendlyGroups, groups);
            Replace(_friendlyUnits, units);
            Replace(_friendlyVehicles, vehicles);
            Replace(_supportAssets, assets);
            _hasCompleteReconciliation = true;
            _lastReconciliationId = observation.ReconciliationId;
            _lastReconciledAtUtc = observation.Envelope.ReceivedAtUtc;
            _pendingPageCount = 0;
            _sequenceGap = false;
            _reconciliationDiagnostic = string.Empty;
            _version++;
            delta = new WorldStateDelta(
                _version, ConnectionChanged: false, SessionReset: false, ResetReason: null,
                UpdatedEntityIds: groups.Values.Select(item => item.Alias)
                    .Concat(units.Values.Select(item => item.Alias))
                    .Concat(vehicles.Values.Select(item => item.Alias))
                    .Concat(assets.Values.Select(item => item.Alias))
                    .Append("reconciliation:complete")
                    .ToArray());
        }
        PublishStateChanged(delta);
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
    }

    internal TelemetryIngestResult ApplyFriendlyDelta(FriendlyForceDeltaObservation observation)
    {
        WorldStateDelta delta;
        lock (_gate)
        {
            string viewerSide = _protocol?.ViewerSide ?? string.Empty;
            if (_hasCompleteReconciliation &&
                !string.Equals(observation.BaseReconciliationId, _lastReconciliationId, StringComparison.Ordinal))
            {
                _reconciliationDiagnostic = "delta_base_mismatch";
            }

            ObservationStamp stamp = new(observation.Envelope.GameTime, observation.Envelope.ReceivedAtUtc, 0);
            List<string> updated = new();
            foreach (FriendlyGroupObservation item in observation.UpsertGroups
                         .Where(item => viewerSide.Length == 0 || item.Side == viewerSide))
            {
                string alias = GetAlias(_groupAliases, ref _nextGroupAlias, "group", item.SourceId);
                _friendlyGroups[item.SourceId] = new FriendlyGroupEntry(alias, item, stamp, 0.95);
                updated.Add(alias);
            }
            foreach (FriendlyUnitObservation item in observation.UpsertUnits
                         .Where(item => viewerSide.Length == 0 || item.Side == viewerSide))
            {
                string alias = GetAlias(_unitAliases, ref _nextUnitAlias, "unit", item.SourceId);
                _friendlyUnits[item.SourceId] = new FriendlyUnitEntry(alias, item, stamp, 0.95);
                updated.Add(alias);
            }
            foreach (FriendlyVehicleObservation item in observation.UpsertVehicles
                         .Where(item => viewerSide.Length == 0 || item.Side == viewerSide))
            {
                string alias = GetAlias(_vehicleAliases, ref _nextVehicleAlias, "vehicle", item.SourceId);
                _friendlyVehicles[item.SourceId] = new FriendlyVehicleEntry(alias, item, stamp, 0.95);
                updated.Add(alias);
            }
            foreach (SupportAssetObservation item in observation.UpsertAssets)
            {
                string alias = GetAlias(_assetAliases, ref _nextAssetAlias, "asset", item.SourceId);
                _supportAssets[item.SourceId] = new SupportAssetEntry(alias, item, stamp, 0.95);
                updated.Add(alias);
            }
            Remove(_friendlyGroups, observation.RemovedGroupIds, _groupAliases, updated);
            Remove(_friendlyUnits, observation.RemovedUnitIds, _unitAliases, updated);
            Remove(_friendlyVehicles, observation.RemovedVehicleIds, _vehicleAliases, updated);
            Remove(_supportAssets, observation.RemovedAssetIds, _assetAliases, updated);
            _version++;
            delta = new WorldStateDelta(
                _version, ConnectionChanged: false, SessionReset: false, ResetReason: null,
                UpdatedEntityIds: updated.Distinct(StringComparer.Ordinal).ToArray());
        }
        PublishStateChanged(delta);
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
    }

    internal TelemetryIngestResult ApplyCapabilities(MissionCapabilitiesObservation observation)
    {
        WorldStateDelta delta;
        lock (_gate)
        {
            string viewerSide = _protocol?.ViewerSide ?? string.Empty;
            ObservationStamp stamp = new(observation.Envelope.GameTime, observation.Envelope.ReceivedAtUtc, 0);
            Dictionary<string, CapabilityEntry> next = observation.Capabilities
                .Where(item => item.Constraints.AllowedRequesterSides.Count == 0 ||
                               viewerSide.Length == 0 ||
                               item.Constraints.AllowedRequesterSides.Contains(viewerSide, StringComparer.Ordinal))
                .ToDictionary(item => item.SourceId, item => new CapabilityEntry(
                    GetAlias(_capabilityAliases, ref _nextCapabilityAlias, "capability", item.SourceId),
                    item, stamp, 1), StringComparer.Ordinal);
            Replace(_capabilities, next);
            _capabilityRegistryVersion = observation.RegistryVersion;
            _version++;
            delta = new WorldStateDelta(
                _version, ConnectionChanged: false, SessionReset: false, ResetReason: null,
                UpdatedEntityIds: next.Values.Select(item => item.Alias).Append("capabilities:registry").ToArray());
        }
        PublishStateChanged(delta);
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
    }

    internal void SetPendingReconciliationPages(int pendingPages)
    {
        WorldStateDelta? delta = null;
        lock (_gate)
        {
            pendingPages = Math.Max(0, pendingPages);
            if (_pendingPageCount != pendingPages)
            {
                _pendingPageCount = pendingPages;
                _version++;
                delta = new WorldStateDelta(
                    _version, false, false, null, new[] { "reconciliation:pending" });
            }
        }
        if (delta is not null) PublishStateChanged(delta);
    }

    internal void MarkReconciliationDegraded(string diagnosticCode, int pendingPages)
    {
        WorldStateDelta delta;
        lock (_gate)
        {
            _reconciliationDiagnostic = diagnosticCode;
            _pendingPageCount = Math.Max(0, pendingPages);
            _version++;
            delta = new WorldStateDelta(
                _version, false, false, null, new[] { "reconciliation:degraded" });
        }
        PublishStateChanged(delta);
    }

    internal TelemetryIngestResult Apply(TelemetryObservation observation)
    {
        TelemetryIngestResult result;
        WorldStateDelta delta;
        lock (_gate)
        {
            WorldResetReason? resetReason = null;
            if (observation.SessionId.Length > 0)
            {
                if (_sourceSessionId.Length > 0 &&
                    !string.Equals(_sourceSessionId, observation.SessionId, StringComparison.Ordinal))
                {
                    return Rejected("telemetry_session_mismatch");
                }
                if (_sourceMissionId.Length > 0 && observation.MissionId.Length > 0 &&
                    !string.Equals(_sourceMissionId, observation.MissionId, StringComparison.Ordinal))
                {
                    return Rejected("telemetry_mission_mismatch");
                }
                if (_sourceSessionId.Length == 0)
                {
                    resetReason = WorldResetReason.ProtocolSessionChanged;
                    StartSession(resetReason.Value, observation.ReceivedAtUtc);
                    _sourceSessionId = observation.SessionId;
                    _sourceMissionId = observation.MissionId;
                }
                if (_protocol is not null &&
                    (!string.Equals(_protocol.WorldName, observation.Map.Name, StringComparison.OrdinalIgnoreCase) ||
                     Math.Abs(_protocol.WorldSizeMeters - observation.Map.SizeMeters) > 0.5))
                {
                    return Rejected("telemetry_world_mismatch");
                }
            }

            if (observation.SessionId.Length == 0)
                resetReason ??= DetectReset(observation);
            if (resetReason is null && IsOutOfOrder(observation))
            {
                return new TelemetryIngestResult(
                    TelemetryIngestStatus.OutOfOrder,
                    KnownContactCount: _contacts.Count,
                    SkippedEntityCount: observation.SkippedEntityCount,
                    DiagnosticCode: "out_of_order");
            }

            if (resetReason is not null && resetReason != WorldResetReason.ProtocolSessionChanged)
            {
                string sourceSession = _sourceSessionId;
                string sourceMission = _sourceMissionId;
                StartSession(resetReason.Value, observation.ReceivedAtUtc);
                _sourceSessionId = sourceSession;
                _sourceMissionId = sourceMission;
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

    private TelemetryIngestResult AcceptProtocolSequence(ProtocolEnvelope envelope)
    {
        if (envelope.Sequence <= _lastProtocolSequence)
            return new TelemetryIngestResult(TelemetryIngestStatus.OutOfOrder, DiagnosticCode: "protocol_out_of_order");
        if (_lastProtocolSequence > 0 && envelope.Sequence > _lastProtocolSequence + 1)
        {
            _sequenceGap = true;
            _reconciliationDiagnostic = "sequence_gap";
        }
        _lastProtocolSequence = envelope.Sequence;
        return new TelemetryIngestResult(TelemetryIngestStatus.Applied);
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
        if (_player is not null && observation.GameTime < _lastGameTime - OrderingToleranceSeconds)
            return WorldResetReason.MissionTimeRegressed;
        if (_player is not null && observation.Frame >= 0 && _lastFrame >= 0 &&
            observation.Frame < _lastFrame - MaterialFrameRegression)
        {
            return WorldResetReason.FrameRegressed;
        }
        return null;
    }

    private bool IsOutOfOrder(TelemetryObservation observation)
    {
        if (_player is null) return false;
        if (observation.GameTime < _lastGameTime) return true;
        return observation.GameTime == _lastGameTime &&
               observation.Frame >= 0 && _lastFrame >= 0 && observation.Frame <= _lastFrame;
    }

    private void StartSession(WorldResetReason reason, DateTimeOffset receivedAtUtc)
    {
        _sessionOrdinal++;
        _sessionId = $"session-{_sessionOrdinal:0000}";
        _sourceSessionId = string.Empty;
        _sourceMissionId = string.Empty;
        _lastResetReason = reason;
        _sessionStartedAtUtc = receivedAtUtc;
        _lastReceivedAtUtc = null;
        _lastGameTime = 0;
        _lastFrame = -1;
        _map = null;
        _player = null;
        _group = null;
        _vehicle = null;
        _protocol = null;
        _contacts.Clear();
        _friendlyGroups.Clear();
        _friendlyUnits.Clear();
        _friendlyVehicles.Clear();
        _supportAssets.Clear();
        _capabilities.Clear();
        _groupAliases.Clear();
        _unitAliases.Clear();
        _vehicleAliases.Clear();
        _assetAliases.Clear();
        _capabilityAliases.Clear();
        _nextContactAlias = 1;
        _nextGroupAlias = 1;
        _nextUnitAlias = 1;
        _nextVehicleAlias = 1;
        _nextAssetAlias = 1;
        _nextCapabilityAlias = 1;
        _lastProtocolSequence = 0;
        _sequenceGap = false;
        _pendingPageCount = 0;
        _hasCompleteReconciliation = false;
        _lastReconciliationId = string.Empty;
        _lastReconciledAtUtc = null;
        _reconciliationDiagnostic = string.Empty;
        _capabilityRegistryVersion = 0;
    }

    private void ClearAuthorityScopedState()
    {
        _friendlyGroups.Clear();
        _friendlyUnits.Clear();
        _friendlyVehicles.Clear();
        _supportAssets.Clear();
        _capabilities.Clear();
        _hasCompleteReconciliation = false;
        _lastReconciliationId = string.Empty;
        _lastReconciledAtUtc = null;
        _pendingPageCount = 0;
        _reconciliationDiagnostic = "authority_changed";
        _capabilityRegistryVersion = 0;
    }

    private List<string> ApplyCurrentState(TelemetryObservation observation)
    {
        bool vehicleChanged = _vehicle is not null || observation.Vehicle is not null;
        ObservationStamp stamp = new(observation.GameTime, observation.ReceivedAtUtc, 0);
        _map = new MapEntry(observation.Map.Name, observation.Map.SizeMeters, observation.Map.Grid,
            observation.Map.Daytime, stamp);
        _player = new PlayerEntry(observation.Player, stamp);

        string groupIdentityInput = observation.Player.GroupSourceId.Length > 0
            ? observation.Player.GroupSourceId
            : $"{observation.Player.Side}\0{observation.Player.GroupLabel}";
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
                new[] { WorldProvenance.Mission }, _map.Stamp, now, 1, null, null, FreshnessProfile.Current),
            _map.Name, _map.SizeMeters, _map.Grid, _map.Daytime);
    }

    private WorldPlayerState? BuildPlayer(DateTimeOffset now)
    {
        if (_player is null) return null;
        PlayerObservation value = _player.Value;
        return new WorldPlayerState(
            Metadata("player:self", EntityIdentityQuality.Stable, WorldProvenance.Player,
                new[] { WorldProvenance.Player }, _player.Stamp, now, 1, value.PositionAtl, null,
                FreshnessProfile.Current),
            value.Side, value.PositionAsl, value.BodyHeading, value.ViewHeading, value.SpeedKph,
            value.Damage, value.LifeState, value.Stance, value.Weapon, value.Magazine, value.Muzzle,
            value.LoadedRounds, value.MatchingMagazineCount, value.MatchingMagazineRounds);
    }

    private WorldGroupState? BuildGroup(DateTimeOffset now)
    {
        if (_group is null) return null;
        return new WorldGroupState(
            Metadata(_group.EntityId, EntityIdentityQuality.BestEffort, WorldProvenance.Group,
                new[] { WorldProvenance.Group }, _group.Stamp, now, 0.9, null, null,
                FreshnessProfile.Current),
            _group.Side, _group.LocalLabel);
    }

    private WorldVehicleState? BuildVehicle(DateTimeOffset now)
    {
        if (_vehicle is null) return null;
        VehicleObservation value = _vehicle.Value;
        return new WorldVehicleState(
            Metadata("vehicle:current", EntityIdentityQuality.Slot, WorldProvenance.Player,
                new[] { WorldProvenance.Player }, _vehicle.Stamp, now, 0.95,
                value.PositionAtl, null, FreshnessProfile.Current),
            value.Class, value.DisplayName, value.Heading, value.SpeedKph, value.Fuel, value.Damage, value.Role);
    }

    private WorldProtocolState? BuildProtocol()
        => _protocol is null ? null : new WorldProtocolState(
            _protocol.Major,
            _protocol.Minor,
            _protocol.ViewerSide,
            _protocol.Visibility,
            _protocol.ReceivedAtUtc,
            _protocol.Features);

    private WorldFriendlyGroupState BuildFriendlyGroup(FriendlyGroupEntry entry, DateTimeOffset now)
    {
        FriendlyGroupObservation value = entry.Value;
        return new WorldFriendlyGroupState(
            Metadata(entry.Alias, IdentityQuality(value.SourceId), WorldProvenance.Group,
                new[] { WorldProvenance.Group }, entry.Stamp, now, entry.BaseConfidence,
                value.PositionAtl, null, FreshnessProfile.Friendly),
            entry.Alias,
            TacticalName(value.Callsign, entry.Alias),
            value.Side,
            ActiveAlias(_unitAliases, _friendlyUnits, value.LeaderSourceId),
            value.UnitSourceIds.Select(id => ActiveAlias(_unitAliases, _friendlyUnits, id))
                .Where(id => id.Length > 0).ToArray(),
            value.Behaviour);
    }

    private WorldFriendlyUnitState BuildFriendlyUnit(FriendlyUnitEntry entry, DateTimeOffset now)
    {
        FriendlyUnitObservation value = entry.Value;
        return new WorldFriendlyUnitState(
            Metadata(entry.Alias, IdentityQuality(value.SourceId), WorldProvenance.Group,
                new[] { WorldProvenance.Group }, entry.Stamp, now, entry.BaseConfidence,
                value.PositionAtl, null, FreshnessProfile.Friendly),
            entry.Alias,
            ActiveAlias(_groupAliases, _friendlyGroups, value.GroupSourceId),
            TacticalName(value.Callsign, entry.Alias),
            value.Side,
            value.Class,
            value.Role,
            value.Alive,
            value.LifeState,
            value.Mobile,
            value.Damage,
            ActiveAlias(_vehicleAliases, _friendlyVehicles, value.VehicleSourceId),
            value.VehicleRole,
            value.MedicalReadiness);
    }

    private WorldFriendlyVehicleState BuildFriendlyVehicle(FriendlyVehicleEntry entry, DateTimeOffset now)
    {
        FriendlyVehicleObservation value = entry.Value;
        return new WorldFriendlyVehicleState(
            Metadata(entry.Alias, IdentityQuality(value.SourceId), WorldProvenance.Group,
                new[] { WorldProvenance.Group }, entry.Stamp, now, entry.BaseConfidence,
                value.PositionAtl, null, FreshnessProfile.Friendly),
            entry.Alias,
            value.Side,
            value.Class,
            value.DisplayName,
            value.Alive,
            value.Mobile,
            value.Damage,
            value.Fuel,
            value.SpeedKph,
            value.CrewUnitSourceIds.Select(id => ActiveAlias(_unitAliases, _friendlyUnits, id))
                .Where(id => id.Length > 0).ToArray(),
            value.CargoCapacity,
            value.EmptyCargoSeats);
    }

    private WorldSupportAssetState BuildSupportAsset(SupportAssetEntry entry, DateTimeOffset now)
    {
        SupportAssetObservation value = entry.Value;
        WorldPosition? position = value.VehicleSourceId.Length > 0 &&
                                  _friendlyVehicles.TryGetValue(value.VehicleSourceId, out FriendlyVehicleEntry? vehicle)
            ? vehicle.Value.PositionAtl
            : null;
        return new WorldSupportAssetState(
            Metadata(entry.Alias, IdentityQuality(value.SourceId), WorldProvenance.Mission,
                new[] { WorldProvenance.Mission }, entry.Stamp, now, entry.BaseConfidence,
                position, null, FreshnessProfile.Friendly),
            entry.Alias,
            value.Kind,
            TacticalName(value.Callsign, entry.Alias),
            value.Provider,
            ActiveAlias(_vehicleAliases, _friendlyVehicles, value.VehicleSourceId),
            value.Status,
            value.Available,
            value.Capacity);
    }

    private WorldCapabilityState BuildCapability(CapabilityEntry entry, DateTimeOffset now)
    {
        CapabilityObservation value = entry.Value;
        return new WorldCapabilityState(
            Metadata(entry.Alias, EntityIdentityQuality.Stable, WorldProvenance.Mission,
                new[] { WorldProvenance.Mission }, entry.Stamp, now, entry.BaseConfidence,
                null, null, FreshnessProfile.Capability),
            entry.Alias,
            value.Capability,
            value.Enabled,
            value.Provider,
            new WorldCapabilityConstraints(
                value.Constraints.MaxConcurrent,
                value.Constraints.AllowedRequesterSides,
                value.Constraints.MaxRangeMeters,
                value.Constraints.MaxPassengers,
                value.Constraints.SupportsCasualties,
                value.Constraints.RequiresConfirmation));
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
                baseConfidence, known?.EstimatedPosition, known?.PositionErrorMeters, FreshnessProfile.Contact),
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
        FreshnessProfile profile)
    {
        double age = Math.Max(0, stamp.SourceAgeAtReceive + (now - stamp.ReceivedAtUtc).TotalSeconds);
        WorldFreshness freshness = ClassifyFreshness(age, profile);
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

    private static WorldFreshness ClassifyFreshness(double ageSeconds, FreshnessProfile profile)
        => profile switch
        {
            FreshnessProfile.Contact => ageSeconds <= 5 ? WorldFreshness.Live
                : ageSeconds <= 30 ? WorldFreshness.Recent
                : ageSeconds <= 120 ? WorldFreshness.Stale
                : WorldFreshness.Historical,
            FreshnessProfile.Friendly => ageSeconds <= 2 ? WorldFreshness.Live
                : ageSeconds <= 15 ? WorldFreshness.Recent
                : ageSeconds <= 45 ? WorldFreshness.Stale
                : WorldFreshness.Historical,
            FreshnessProfile.Capability => ageSeconds <= 30 ? WorldFreshness.Live
                : ageSeconds <= 60 ? WorldFreshness.Recent
                : ageSeconds <= 120 ? WorldFreshness.Stale
                : WorldFreshness.Historical,
            _ => ageSeconds <= 1 ? WorldFreshness.Live
                : ageSeconds <= 5 ? WorldFreshness.Recent
                : ageSeconds <= 30 ? WorldFreshness.Stale
                : WorldFreshness.Historical
        };

    private static EntityIdentityQuality IdentityQuality(string sourceId)
        => sourceId.StartsWith("net:", StringComparison.Ordinal) ||
           sourceId.StartsWith("asset:", StringComparison.Ordinal) ||
           sourceId.StartsWith("capability:", StringComparison.Ordinal)
            ? EntityIdentityQuality.Stable
            : EntityIdentityQuality.BestEffort;

    private static string TacticalName(string value, string alias)
        => string.IsNullOrWhiteSpace(value) ? alias : value;

    private static string GetAlias(
        IDictionary<string, string> aliases, ref int counter, string prefix, string sourceId)
    {
        if (aliases.TryGetValue(sourceId, out string? alias)) return alias;
        alias = $"{prefix}-{counter++:000}";
        aliases.Add(sourceId, alias);
        return alias;
    }

    private static string Alias(IReadOnlyDictionary<string, string> aliases, string sourceId)
        => sourceId.Length > 0 && aliases.TryGetValue(sourceId, out string? alias) ? alias : string.Empty;

    private static string ActiveAlias<T>(
        IReadOnlyDictionary<string, string> aliases,
        IReadOnlyDictionary<string, T> entities,
        string sourceId)
        => sourceId.Length > 0 && entities.ContainsKey(sourceId) ? Alias(aliases, sourceId) : string.Empty;

    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        foreach ((TKey key, TValue value) in source) target.Add(key, value);
    }

    private static void Remove<T>(
        IDictionary<string, T> target,
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, string> aliases,
        ICollection<string> updated)
    {
        foreach (string sourceId in sourceIds)
        {
            if (target.Remove(sourceId) && aliases.TryGetValue(sourceId, out string? alias)) updated.Add(alias);
        }
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

    private static TelemetryIngestResult Rejected(string code)
        => new(TelemetryIngestStatus.Rejected, DiagnosticCode: code);

    private enum FreshnessProfile
    {
        Current,
        Contact,
        Friendly,
        Capability
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

    private sealed record ProtocolEntry(
        int Major,
        int Minor,
        string WorldName,
        double WorldSizeMeters,
        string ViewerSide,
        string Visibility,
        DateTimeOffset ReceivedAtUtc,
        IReadOnlyList<WorldProtocolFeatureState> Features);

    private sealed record FriendlyGroupEntry(
        string Alias, FriendlyGroupObservation Value, ObservationStamp Stamp, double BaseConfidence);

    private sealed record FriendlyUnitEntry(
        string Alias, FriendlyUnitObservation Value, ObservationStamp Stamp, double BaseConfidence);

    private sealed record FriendlyVehicleEntry(
        string Alias, FriendlyVehicleObservation Value, ObservationStamp Stamp, double BaseConfidence);

    private sealed record SupportAssetEntry(
        string Alias, SupportAssetObservation Value, ObservationStamp Stamp, double BaseConfidence);

    private sealed record CapabilityEntry(
        string Alias, CapabilityObservation Value, ObservationStamp Stamp, double BaseConfidence);

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
