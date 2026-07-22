using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public partial class MainWindow : Window
{
    private const string TelemetrySchema = "arma-ai-bridge/arma3/telemetry-v1";
    private const string QueryResultSchema = "arma-ai-bridge/arma3/query-result-v1";
    private const string CommandSchema = "arma-ai-bridge/command-v1";

    private readonly LogService _log = new();
    private readonly SettingsService _settingsService = new();
    private readonly TelemetryPipeServer _pipeServer;
    private readonly WorldStateStore _worldStateStore;
    private readonly WorldSnapshotBuilder _worldSnapshotBuilder;
    private readonly TelemetryIngestService _telemetryIngestService;
    private long _snapshotCount;
    private string? _pendingRequestId;

    public MainWindow()
    {
        InitializeComponent();
        _pipeServer = new TelemetryPipeServer(_log);
        _worldStateStore = new WorldStateStore();
        _worldSnapshotBuilder = new WorldSnapshotBuilder(_worldStateStore);
        _telemetryIngestService = new TelemetryIngestService(
            _pipeServer, _worldStateStore, _log);
        _log.EntryWritten += OnLogEntryWritten;
        _pipeServer.ClientConnectionChanged += OnClientConnectionChanged;
        _pipeServer.MessageReceived += OnBridgeMessageReceived;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadSettingsAsync();
            await StartListenerAsync();
            _log.Info("ArmA AI Bridge v0.6.0 started.");
        }
        catch (Exception exception)
        {
            _log.Error("Startup failed", exception);
            MessageBox.Show(this, exception.Message, "ArmA AI Bridge startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _assistantPanel?.Dispose();
        _worldStateDiagnosticsPanel?.Dispose();
        _telemetryIngestService.Dispose();
        await _pipeServer.DisposeAsync();
    }

    private async Task LoadSettingsAsync()
    {
        AppSettings settings;
        try
        {
            settings = await _settingsService.LoadAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Error("Settings file could not be loaded", exception);
            SettingsStatusText.Text = "Settings could not be loaded. Saving will replace the invalid file.";
            return;
        }

        try
        {
            OpenAiKeyBox.Password = DpapiService.Unprotect(settings.OpenAiApiKeyProtected);
            ElevenLabsKeyBox.Password = DpapiService.Unprotect(settings.ElevenLabsApiKeyProtected);
            ElevenLabsVoiceIdBox.Text = settings.ElevenLabsVoiceId;
            _log.Info("Encrypted settings loaded.");
        }
        catch (Exception exception) when (exception is FormatException or System.ComponentModel.Win32Exception)
        {
            _log.Error("Settings could not be decrypted for the current Windows user", exception);
            SettingsStatusText.Text = "Stored credentials could not be decrypted. Save them again.";
        }
    }

    private async Task StartListenerAsync()
    {
        await _pipeServer.StartAsync();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SendQueryButton.IsEnabled = false;
        SetConnectionStatus("Listening", "#E7B85A");
        FooterText.Text = "Waiting for Arma 3 bridge";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartListenerAsync();
        }
        catch (Exception exception)
        {
            _log.Error("Could not start listener", exception);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await _pipeServer.StopAsync();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SendQueryButton.IsEnabled = false;
        SetConnectionStatus("Stopped", "#D86A6A");
        FooterText.Text = "Bridge listener stopped";
    }

    private async void SendQueryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string shape = ReadSelectedTag(QueryShapeBox, "cone");
            string direction = ReadSelectedTag(QueryDirectionBox, "view");
            double range = ParseBoundedDouble(QueryRangeBox.Text, "Range", 25, 1500);
            double angle = ParseBoundedDouble(QueryAngleBox.Text, "Cone angle", 5, 180);
            int limit = ParseBoundedInt(QueryLimitBox.Text, "Result limit", 1, 50);
            string[] categories = CollectSelectedCategories();
            if (categories.Length == 0)
            {
                throw new InvalidOperationException("Select at least one object category.");
            }

            string requestId = Guid.NewGuid().ToString("N");
            Dictionary<string, object> parameters = new()
            {
                ["origin"] = "player",
                ["shape"] = shape,
                ["direction"] = direction,
                [shape == "circle" ? "radiusMeters" : "rangeMeters"] = range,
                ["categories"] = categories,
                ["maxResultsPerCategory"] = limit
            };
            if (shape == "cone")
            {
                parameters["angleDegrees"] = angle;
            }

            string commandJson = JsonSerializer.Serialize(new
            {
                schema = CommandSchema,
                requestId,
                command = "query_environment",
                parameters
            });

            bool sent = await _pipeServer.SendCommandAsync(commandJson);
            if (!sent)
            {
                QueryStatusText.Text = "Arma is not connected. Start a mission with the mod loaded and try again.";
                return;
            }

            _pendingRequestId = requestId;
            QueryStatusText.Text = $"Query {requestId[..8]} sent. Waiting for Arma...";
            LastQueryText.Text = $"Pending · {shape} · {range:N0} m · {string.Join(", ", categories)}";
            QueryResultTextBox.Clear();
            _log.Info($"Environment query sent: {requestId}, shape={shape}, range={range:N0} m, categories={string.Join(',', categories)}.");
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            QueryStatusText.Text = exception.Message;
            _log.Warn($"Environment query validation failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            QueryStatusText.Text = "Query failed. See Logs.";
            _log.Error("Could not send environment query", exception);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings settings = new()
            {
                OpenAiApiKeyProtected = DpapiService.Protect(OpenAiKeyBox.Password.Trim()),
                ElevenLabsApiKeyProtected = DpapiService.Protect(ElevenLabsKeyBox.Password.Trim()),
                ElevenLabsVoiceId = ElevenLabsVoiceIdBox.Text.Trim()
            };
            await _settingsService.SaveAsync(settings);
            SettingsStatusText.Text = $"Saved at {DateTime.Now:T}.";
            _log.Info("Encrypted API settings saved. Credential values were not logged.");
        }
        catch (Exception exception)
        {
            SettingsStatusText.Text = "Save failed. See Logs.";
            _log.Error("Could not save encrypted settings", exception);
        }
    }

    private void ClearSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAiKeyBox.Clear();
        ElevenLabsKeyBox.Clear();
        ElevenLabsVoiceIdBox.Clear();
        SettingsStatusText.Text = "Fields cleared locally. Click Save to overwrite stored values.";
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDirectory, UseShellExecute = true });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogListBox.Items.Clear();

    private void OnLogEntryWritten(string line)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            LogListBox.Items.Add(line);
            while (LogListBox.Items.Count > 1000)
            {
                LogListBox.Items.RemoveAt(0);
            }
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
            }
        });
    }

    private void OnClientConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            SendQueryButton.IsEnabled = connected;
            if (connected)
            {
                SetConnectionStatus("Arma connected", "#70D6A3");
                FooterText.Text = "Receiving telemetry and accepting map queries";
                QueryStatusText.Text = "Ready to send a dynamic query.";
            }
            else if (_pipeServer.IsRunning)
            {
                SetConnectionStatus("Listening", "#E7B85A");
                FooterText.Text = "Waiting for Arma 3 bridge";
                QueryStatusText.Text = "Connect Arma 3 to enable queries.";
            }
        });
    }

    private void OnBridgeMessageReceived(string json)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                string schema = ReadString(root, "schema");

                if (schema == QueryResultSchema)
                {
                    RawTelemetryTextBox.Text = PrettyPrintJson(json);
                    HandleQueryResult(root, json);
                }
                else if (schema == TelemetrySchema)
                {
                    RawTelemetryTextBox.Text = RedactTelemetryForDisplay(root);
                    HandleTelemetry(root);
                }
                else if (TelemetryIngestService.IsWorldStateSchema(schema))
                {
                    RawTelemetryTextBox.Text = JsonSerializer.Serialize(new
                    {
                        schema,
                        status = "Ingested into World State; source identifiers are hidden in this view."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    RawTelemetryTextBox.Text = PrettyPrintJson(json);
                    _log.Warn($"Received bridge message with unknown schema '{schema}'.");
                }
            }
            catch (JsonException exception)
            {
                _log.Warn($"Received invalid bridge JSON: {exception.Message}");
            }
        });
    }

    private void HandleTelemetry(JsonElement root)
    {
        _snapshotCount++;
        ReceivedText.Text = $"{DateTime.Now:T} · #{_snapshotCount:N0}";

        if (root.TryGetProperty("map", out JsonElement map))
        {
            string mapName = ReadString(map, "name", "Unknown");
            double mapSize = ReadDouble(map, "sizeMeters");
            MapText.Text = mapSize > 0 ? $"{mapName} · {mapSize:N0} m" : mapName;
        }

        if (root.TryGetProperty("player", out JsonElement player))
        {
            PositionText.Text = FormatVector(player, "positionATL");
            HeadingText.Text = $"{ReadDouble(player, "viewHeading"):N1}°";
        }

        ContactsText.Text = root.TryGetProperty("contacts", out JsonElement contacts) && contacts.ValueKind == JsonValueKind.Array
            ? contacts.GetArrayLength().ToString(CultureInfo.InvariantCulture)
            : "0";
    }

    private void HandleQueryResult(JsonElement root, string json)
    {
        string requestId = ReadString(root, "requestId");
        bool ok = root.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        QueryResultTextBox.Text = PrettyPrintJson(json);

        if (!ok)
        {
            string error = ReadString(root, "error", "Unknown query error");
            QueryStatusText.Text = $"Query failed: {error}";
            LastQueryText.Text = "Failed";
            _log.Warn($"Environment query failed: {requestId}: {error}");
            return;
        }

        string summary = BuildQuerySummary(root);
        QueryStatusText.Text = $"Query {ShortId(requestId)} completed at {DateTime.Now:T}.";
        LastQueryText.Text = summary;
        _log.Info($"Environment query completed: {requestId}. {summary}");

        if (string.Equals(_pendingRequestId, requestId, StringComparison.Ordinal))
        {
            _pendingRequestId = null;
        }
    }

    private static string BuildQuerySummary(JsonElement root)
    {
        if (!root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("categories", out JsonElement categories) ||
            categories.ValueKind != JsonValueKind.Object)
        {
            return "Query completed";
        }

        List<string> parts = new();
        foreach (JsonProperty category in categories.EnumerateObject())
        {
            int count = ReadInt(category.Value, "totalCount");
            double nearest = ReadDouble(category.Value, "nearestDistanceMeters");
            parts.Add(nearest >= 0
                ? $"{category.Name}: {count} (nearest {nearest:N0} m)"
                : $"{category.Name}: {count}");
        }
        return parts.Count == 0 ? "No matching map objects" : string.Join(" · ", parts);
    }

    private string[] CollectSelectedCategories()
    {
        List<string> categories = new();
        if (BuildingsCheckBox.IsChecked == true) categories.Add("building");
        if (VegetationCheckBox.IsChecked == true) categories.Add("vegetation");
        if (RoadsCheckBox.IsChecked == true) categories.Add("road");
        if (WallsCheckBox.IsChecked == true) categories.Add("wall");
        if (RocksCheckBox.IsChecked == true) categories.Add("rock");
        return categories.ToArray();
    }

    private static string ReadSelectedTag(ComboBox comboBox, string fallback)
        => comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : fallback;

    private static double ParseBoundedDouble(string text, string name, double minimum, double maximum)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            throw new FormatException($"{name} must be a number.");
        }
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum:N0} and {maximum:N0}.");
        }
        return value;
    }

    private static int ParseBoundedInt(string text, string name, int minimum, int maximum)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new FormatException($"{name} must be a whole number.");
        }
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
        }
        return value;
    }

    private static string FormatVector(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement vector) || vector.ValueKind != JsonValueKind.Array)
        {
            return "—";
        }

        double[] values = vector.EnumerateArray()
            .Take(3)
            .Select(element => element.ValueKind == JsonValueKind.Number ? element.GetDouble() : 0)
            .ToArray();
        return values.Length >= 2 ? string.Join(" / ", values.Select(value => value.ToString("N1"))) : "—";
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string RedactTelemetryForDisplay(JsonElement root)
    {
        JsonObject? message = JsonNode.Parse(root.GetRawText()) as JsonObject;
        if (message is null) return "Telemetry received.";
        message.Remove("missionId");
        message.Remove("sessionId");
        if (message["player"] is JsonObject player)
        {
            player.Remove("uid");
            player.Remove("name");
            player.Remove("id");
            player.Remove("groupId");
        }
        if (message["vehicle"] is JsonObject vehicle) vehicle.Remove("id");
        RedactArrayIds(message["contacts"] as JsonArray);
        RedactArrayIds(message["sensorContacts"] as JsonArray);
        return message.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RedactArrayIds(JsonArray? array)
    {
        if (array is null) return;
        foreach (JsonNode? item in array)
        {
            if (item is JsonObject value) value.Remove("id");
        }
    }

    private static string ShortId(string requestId)
        => requestId.Length > 8 ? requestId[..8] : requestId;

    private static string ReadString(JsonElement parent, string propertyName, string fallback = "")
        => parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static double ReadDouble(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static int ReadInt(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private void SetConnectionStatus(string text, string color)
    {
        ConnectionStatusText.Text = text;
        ConnectionIndicator.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }
}
