using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public partial class MainWindow : Window
{
    private readonly LogService _log = new();
    private readonly SettingsService _settingsService = new();
    private readonly TelemetryPipeServer _pipeServer;
    private long _snapshotCount;

    public MainWindow()
    {
        InitializeComponent();
        _pipeServer = new TelemetryPipeServer(_log);
        _log.EntryWritten += OnLogEntryWritten;
        _pipeServer.ClientConnectionChanged += OnClientConnectionChanged;
        _pipeServer.TelemetryReceived += OnTelemetryReceived;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadSettingsAsync();
            await StartListenerAsync();
            _log.Info("ArmA AI Bridge v0.1.0 started.");
        }
        catch (Exception exception)
        {
            _log.Error("Startup failed", exception);
            MessageBox.Show(this, exception.Message, "ArmA AI Bridge startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => await _pipeServer.DisposeAsync();

    private async Task LoadSettingsAsync()
    {
        AppSettings settings;
        try { settings = await _settingsService.LoadAsync(); }
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
            AssemblyAiKeyBox.Password = DpapiService.Unprotect(settings.AssemblyAiApiKeyProtected);
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
        SetConnectionStatus("Listening", "#E7B85A");
        FooterText.Text = "Waiting for Arma 3 bridge";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try { await StartListenerAsync(); }
        catch (Exception exception) { _log.Error("Could not start listener", exception); }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await _pipeServer.StopAsync();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetConnectionStatus("Stopped", "#D86A6A");
        FooterText.Text = "Bridge listener stopped";
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings settings = new()
            {
                OpenAiApiKeyProtected = DpapiService.Protect(OpenAiKeyBox.Password.Trim()),
                ElevenLabsApiKeyProtected = DpapiService.Protect(ElevenLabsKeyBox.Password.Trim()),
                AssemblyAiApiKeyProtected = DpapiService.Protect(AssemblyAiKeyBox.Password.Trim()),
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
        AssemblyAiKeyBox.Clear();
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
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            LogListBox.Items.Add(line);
            while (LogListBox.Items.Count > 1000) LogListBox.Items.RemoveAt(0);
            if (LogListBox.Items.Count > 0) LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
        });
    }

    private void OnClientConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                SetConnectionStatus("Arma connected", "#70D6A3");
                FooterText.Text = "Receiving Arma 3 telemetry";
            }
            else if (_pipeServer.IsRunning)
            {
                SetConnectionStatus("Listening", "#E7B85A");
                FooterText.Text = "Waiting for Arma 3 bridge";
            }
        });
    }

    private void OnTelemetryReceived(string json)
    {
        Dispatcher.Invoke(() =>
        {
            _snapshotCount++;
            RawTelemetryTextBox.Text = PrettyPrintJson(json);
            ReceivedText.Text = $"{DateTime.Now:T} · #{_snapshotCount:N0}";
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
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
                ContactsText.Text = root.TryGetProperty("contacts", out JsonElement contacts) ? contacts.GetArrayLength().ToString() : "0";
                ProbeText.Text = BuildProbeSummary(root);
            }
            catch (JsonException exception) { _log.Warn($"Received invalid telemetry JSON: {exception.Message}"); }
        });
    }

    private static string BuildProbeSummary(JsonElement root)
    {
        if (!root.TryGetProperty("environment", out JsonElement environment) || !environment.TryGetProperty("probes", out JsonElement probes) || probes.ValueKind != JsonValueKind.Array) return "—";
        List<string> summaries = new();
        foreach (JsonElement probe in probes.EnumerateArray())
        {
            double distance = ReadDouble(probe, "distanceMeters");
            int buildings = ReadInt(probe, "buildingCount");
            int vegetation = ReadInt(probe, "vegetationCount");
            summaries.Add($"{distance:N0} m: {buildings} buildings, {vegetation} vegetation");
        }
        return summaries.Count == 0 ? "—" : string.Join(Environment.NewLine, summaries);
    }

    private static string FormatVector(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement vector) || vector.ValueKind != JsonValueKind.Array) return "—";
        double[] values = vector.EnumerateArray().Take(3).Select(element => element.ValueKind == JsonValueKind.Number ? element.GetDouble() : 0).ToArray();
        return values.Length >= 2 ? string.Join(" / ", values.Select(value => value.ToString("N1"))) : "—";
    }

    private static string PrettyPrintJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    private static string ReadString(JsonElement parent, string propertyName, string fallback = "")
        => parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static double ReadDouble(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static int ReadInt(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private void SetConnectionStatus(string text, string color)
    {
        ConnectionStatusText.Text = text;
        ConnectionIndicator.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }
}
