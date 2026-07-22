using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class MapKnowledgeService : IDisposable
{
    private const int MaximumPendingPages = 4096;
    private readonly MapKnowledgeDatabase _database;
    private readonly TelemetryPipeServer? _pipe;
    private readonly LogService? _log;
    private readonly Func<string, CancellationToken, Task<bool>>? _sendCommand;
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly Dictionary<int, PendingTile> _pendingTiles = new();
    private string _activeSessionId = string.Empty;
    private string _activeExportId = string.Empty;
    private string _activeFingerprint = string.Empty;
    private bool _featureAvailable;
    private bool _disposed;

    public MapKnowledgeService(
        MapKnowledgeDatabase database,
        TelemetryPipeServer? pipe = null,
        LogService? log = null,
        Func<string, CancellationToken, Task<bool>>? sendCommand = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pipe = pipe;
        _log = log;
        _sendCommand = sendCommand ?? (pipe is null ? null : pipe.SendCommandAsync);
        if (_pipe is not null)
        {
            _pipe.MessageReceived += OnMessageReceived;
            _pipe.ClientConnectionChanged += OnConnectionChanged;
        }
    }

    public event Action<MapKnowledgeDiagnostics>? StateChanged;

    public MapKnowledgeDatabase Database => _database;

    public MapKnowledgeDiagnostics GetDiagnostics()
        => _database.GetDiagnostics(_activeExportId.Length > 0, PendingPageCount());

    public async Task ProcessMessageAsync(string json, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024) return;
        await _processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string schema = MapKnowledgeProtocol.ReadOptionalString(root, "schema");
            switch (schema)
            {
                case MapKnowledgeProtocol.HandshakeSchema:
                    ProcessHandshake(root);
                    break;
                case MapKnowledgeProtocol.ManifestSchema:
                    await ProcessManifestAsync(root, cancellationToken).ConfigureAwait(false);
                    break;
                case MapKnowledgeProtocol.TileSchema:
                    ProcessTile(root);
                    break;
                case MapKnowledgeProtocol.ProgressSchema:
                    ProcessProgress(root);
                    break;
            }
        }
        catch (JsonException)
        {
            Fail("invalid_map_json");
        }
        catch (InvalidDataException exception)
        {
            Fail(exception.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            Fail("sqlite_failure");
        }
        finally
        {
            _processLock.Release();
        }
    }

    private void ProcessHandshake(JsonElement root)
    {
        bool supported = MapKnowledgeProtocol.HandshakeSupportsMapExport(root, out string sessionId);
        if (!string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
        {
            _pendingTiles.Clear();
            _activeExportId = string.Empty;
            _activeSessionId = sessionId;
        }
        _featureAvailable = supported;
        if (!supported && _database.DatabasePath.Length > 0)
            _database.SetReadiness(MapKnowledgeReadiness.Unavailable, "feature_unavailable");
        Publish();
    }

    private async Task ProcessManifestAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!_featureAvailable) throw new InvalidDataException("feature_unavailable");
        MapManifestData manifest = MapKnowledgeProtocol.ParseManifest(root);
        if (!string.Equals(manifest.SessionId, _activeSessionId, StringComparison.Ordinal))
            throw new InvalidDataException("session_mismatch");
        string fingerprint = MapFingerprint.Compute(manifest);

        if (_activeFingerprint.Length > 0 && !string.Equals(_activeFingerprint, fingerprint, StringComparison.Ordinal))
        {
            await CancelActiveExportAsync(cancellationToken).ConfigureAwait(false);
            _database.MarkCurrentStale();
            _pendingTiles.Clear();
        }

        _activeFingerprint = fingerprint;
        MapKnowledgeDiagnostics diagnostics = _database.Activate(manifest, fingerprint);
        if (diagnostics.Readiness == MapKnowledgeReadiness.Ready && diagnostics.CompletedTiles == diagnostics.TotalTiles)
        {
            _activeExportId = string.Empty;
            Publish();
            return;
        }
        if (_activeExportId.Length > 0) return;

        int startTile = _database.GetFirstMissingTile();
        _activeExportId = "export-" + Guid.NewGuid().ToString("N");
        _database.SetReadiness(MapKnowledgeReadiness.Indexing);
        string command = MapKnowledgeProtocol.CreateStartCommand(
            "map-index-" + Guid.NewGuid().ToString("N"), _activeSessionId, _activeExportId,
            fingerprint, manifest.Export, startTile);
        bool sent = _sendCommand is null || await _sendCommand(command, cancellationToken).ConfigureAwait(false);
        if (!sent)
        {
            _activeExportId = string.Empty;
            _database.SetReadiness(startTile > 0 ? MapKnowledgeReadiness.Partial : MapKnowledgeReadiness.Unavailable,
                "bridge_disconnected");
        }
        Publish();
    }

    private void ProcessTile(JsonElement root)
    {
        MapTilePageData page = MapKnowledgeProtocol.ParseTilePage(root);
        ValidateActive(page.SessionId, page.ExportId, page.Fingerprint, page.IndexVersion);
        MapManifestData manifest = _database.ActiveManifest ?? throw new InvalidDataException("manifest_unavailable");
        ValidateTileBounds(page.Tile, manifest);

        if (!_pendingTiles.TryGetValue(page.Tile.Ordinal, out PendingTile? pending))
        {
            if (PendingPageCount() + page.PageCount > MaximumPendingPages)
                throw new InvalidDataException("pending_page_limit_exceeded");
            pending = new PendingTile(page.Tile, page.PageCount);
            _pendingTiles.Add(page.Tile.Ordinal, pending);
        }
        pending.Add(page);
        if (!pending.IsComplete)
        {
            Publish();
            return;
        }

        _database.CommitTile(pending.Tile, pending.PagesInOrder());
        _pendingTiles.Remove(page.Tile.Ordinal);
        Publish();
    }

    private void ProcessProgress(JsonElement root)
    {
        MapKnowledgeProtocol.ValidateEnvelope(root, MapKnowledgeProtocol.ProgressSchema,
            "exportId", "fingerprint", "indexVersion", "status", "completedTiles", "totalTiles", "nextTileOrdinal", "errorCode");
        string sessionId = MapKnowledgeProtocol.ReadIdentifier(root, "sessionId");
        string exportId = MapKnowledgeProtocol.ReadIdentifier(root, "exportId");
        string fingerprint = MapKnowledgeProtocol.ReadFingerprint(root, "fingerprint");
        int indexVersion = MapKnowledgeProtocol.ReadInt(root, "indexVersion", 1, 1);
        ValidateActive(sessionId, exportId, fingerprint, indexVersion);
        string status = MapKnowledgeProtocol.ReadString(root, "status", 16);
        int total = MapKnowledgeProtocol.ReadInt(root, "totalTiles", 1, 16384);
        int completed = MapKnowledgeProtocol.ReadInt(root, "completedTiles", 0, total);
        int next = MapKnowledgeProtocol.ReadInt(root, "nextTileOrdinal", 0, total);
        if (!root.TryGetProperty("errorCode", out JsonElement error)) throw new InvalidDataException("missing_errorCode");
        string? errorCode = error.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => MapKnowledgeProtocol.ReadString(root, "errorCode", 64),
            _ => throw new InvalidDataException("invalid_errorCode")
        };
        if (errorCode is not null && !string.Equals(errorCode, "tile_page_limit_exceeded", StringComparison.Ordinal))
            throw new InvalidDataException("invalid_errorCode");
        MapManifestData manifest = _database.ActiveManifest ?? throw new InvalidDataException("manifest_unavailable");
        if (total != manifest.Export.TotalTiles || next < completed) throw new InvalidDataException("invalid_progress");

        switch (status)
        {
            case "started":
            case "indexing":
                _database.SetReadiness(MapKnowledgeReadiness.Indexing);
                break;
            case "completed":
                if (_pendingTiles.Count != 0) throw new InvalidDataException("completed_with_pending_pages");
                _database.CompleteIndex();
                _activeExportId = string.Empty;
                break;
            case "cancelled":
                _activeExportId = string.Empty;
                _pendingTiles.Clear();
                _database.SetReadiness(_database.GetFirstMissingTile() > 0
                    ? MapKnowledgeReadiness.Partial : MapKnowledgeReadiness.Unavailable, "export_cancelled");
                break;
            case "failed":
                throw new InvalidDataException(errorCode ?? "export_failed");
            default:
                throw new InvalidDataException("invalid_progress_status");
        }
        Publish();
    }

    private async Task CancelActiveExportAsync(CancellationToken cancellationToken)
    {
        if (_activeExportId.Length == 0 || _sendCommand is null) return;
        string command = MapKnowledgeProtocol.CreateCancelCommand(
            "map-index-" + Guid.NewGuid().ToString("N"), _activeSessionId, _activeExportId, _activeFingerprint);
        await _sendCommand(command, cancellationToken).ConfigureAwait(false);
        _activeExportId = string.Empty;
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected || _database.DatabasePath.Length == 0) return;
        _activeExportId = string.Empty;
        _pendingTiles.Clear();
        try
        {
            if (_database.GetReadiness() == MapKnowledgeReadiness.Ready)
            {
                Publish();
                return;
            }
            _database.SetReadiness(_database.GetFirstMissingTile() > 0
                ? MapKnowledgeReadiness.Partial : MapKnowledgeReadiness.Unavailable, "bridge_disconnected");
            Publish();
        }
        catch (Exception) { }
    }

    private void OnMessageReceived(string json) => _ = ProcessSafelyAsync(json);

    private async Task ProcessSafelyAsync(string json)
    {
        try { await ProcessMessageAsync(json).ConfigureAwait(false); }
        catch (Exception exception) { _log?.Error("Map Knowledge message processing failed", exception); }
    }

    private void ValidateActive(string sessionId, string exportId, string fingerprint, int indexVersion)
    {
        if (!string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal)) throw new InvalidDataException("session_mismatch");
        if (!string.Equals(exportId, _activeExportId, StringComparison.Ordinal)) throw new InvalidDataException("export_mismatch");
        if (!string.Equals(fingerprint, _activeFingerprint, StringComparison.Ordinal)) throw new InvalidDataException("fingerprint_mismatch");
        if (indexVersion != MapFingerprint.IndexVersion) throw new InvalidDataException("index_version_mismatch");
    }

    private static void ValidateTileBounds(MapTileBounds tile, MapManifestData manifest)
    {
        int columns = (int)Math.Ceiling(manifest.WorldSizeMeters / manifest.Export.TileSizeMeters);
        if (tile.Ordinal >= manifest.Export.TotalTiles || tile.Column != tile.Ordinal % columns || tile.Row != tile.Ordinal / columns)
            throw new InvalidDataException("invalid_tile_ordinal");
        double expectedMinX = tile.Column * manifest.Export.TileSizeMeters;
        double expectedMinY = tile.Row * manifest.Export.TileSizeMeters;
        double expectedMaxX = Math.Min(expectedMinX + manifest.Export.TileSizeMeters, manifest.WorldSizeMeters);
        double expectedMaxY = Math.Min(expectedMinY + manifest.Export.TileSizeMeters, manifest.WorldSizeMeters);
        if (Math.Abs(tile.MinX - expectedMinX) > 0.01 || Math.Abs(tile.MinY - expectedMinY) > 0.01 ||
            Math.Abs(tile.MaxX - expectedMaxX) > 0.01 || Math.Abs(tile.MaxY - expectedMaxY) > 0.01)
            throw new InvalidDataException("invalid_tile_bounds");
    }

    private int PendingPageCount() => _pendingTiles.Values.Sum(tile => tile.ReceivedPageCount);

    private void Fail(string code)
    {
        _activeExportId = string.Empty;
        _pendingTiles.Clear();
        if (_database.DatabasePath.Length > 0)
        {
            try { _database.SetReadiness(MapKnowledgeReadiness.Failed, code); } catch { }
        }
        _log?.Warn("Map Knowledge rejected a message: " + code);
        Publish();
    }

    private void Publish()
    {
        MapKnowledgeDiagnostics diagnostics = GetDiagnostics();
        try { StateChanged?.Invoke(diagnostics); } catch (Exception exception) { _log?.Error("Map Knowledge subscriber failed", exception); }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_pipe is not null)
        {
            _pipe.MessageReceived -= OnMessageReceived;
            _pipe.ClientConnectionChanged -= OnConnectionChanged;
        }
        _processLock.Dispose();
    }

    private sealed class PendingTile
    {
        private readonly string?[] _pages;
        public PendingTile(MapTileBounds tile, int pageCount) { Tile = tile; _pages = new string[pageCount]; }
        public MapTileBounds Tile { get; }
        public int ReceivedPageCount => _pages.Count(page => page is not null);
        public bool IsComplete => ReceivedPageCount == _pages.Length;
        public void Add(MapTilePageData page)
        {
            if (page.PageCount != _pages.Length || page.Tile != Tile) throw new InvalidDataException("inconsistent_tile_page");
            string? existing = _pages[page.PageIndex];
            if (existing is not null && !string.Equals(existing, page.RecordsJson, StringComparison.Ordinal))
                throw new InvalidDataException("conflicting_tile_page");
            _pages[page.PageIndex] = page.RecordsJson;
        }
        public IReadOnlyList<string> PagesInOrder() => _pages.Select(page => page!).ToArray();
    }
}
