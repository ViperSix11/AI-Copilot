using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class WorldStateDiagnosticsPanel : UserControl, IDisposable
{
    private readonly WorldStateStore _store;
    private readonly WorldSnapshotBuilder _snapshots;
    private readonly DispatcherTimer _refreshTimer;
    private readonly TextBox _summary = ReadOnlyTextBox(wrap: true);
    private readonly TextBox _snapshot = ReadOnlyTextBox(wrap: false);
    private bool _disposed;

    public WorldStateDiagnosticsPanel(WorldStateStore store, WorldSnapshotBuilder snapshots)
    {
        _store = store;
        _snapshots = snapshots;
        Content = BuildUi();
        _store.StateChanged += OnStateChanged;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();
        Refresh();
    }

    private UIElement BuildUi()
    {
        Grid grid = new() { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid summaryPanel = Panel("Local world state", _summary);
        grid.Children.Add(summaryPanel);
        Grid snapshotPanel = Panel("OpenAI current-situation snapshot (read only)", _snapshot);
        Grid.SetColumn(snapshotPanel, 2);
        grid.Children.Add(snapshotPanel);
        return grid;
    }

    private static Grid Panel(string title, UIElement content)
    {
        Grid grid = new();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        Border border = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 81)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Child = content
        };
        Grid.SetRow(border, 1);
        grid.Children.Add(border);
        return grid;
    }

    private static TextBox ReadOnlyTextBox(bool wrap) => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 12,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent
    };

    private void OnStateChanged(WorldStateDelta delta)
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (_disposed) return;
        WorldStateView view = _store.GetCurrentView();
        _summary.Text = BuildSummary(view);
        if (_snapshots.TryBuildCurrentSituation(out string json))
        {
            using JsonDocument document = JsonDocument.Parse(json);
            _snapshot.Text = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        else
        {
            _snapshot.Text = "Waiting for the first valid telemetry observation.";
        }
    }

    private static string BuildSummary(WorldStateView view)
    {
        StringBuilder text = new();
        text.AppendLine($"Connection: {(view.IsConnected ? "connected" : "disconnected")}");
        if (!view.HasTelemetry)
        {
            text.AppendLine("Session: waiting for telemetry");
            text.AppendLine();
            text.AppendLine("Identity note: telemetry-v1 has no explicit mission ID. Same-map mission changes are detectable only when game time or frame evidence regresses.");
            return text.ToString();
        }

        text.AppendLine($"Session: {view.SessionId}");
        text.AppendLine($"Last reset: {view.LastResetReason}");
        text.AppendLine($"Game time / frame: {view.LastObservedAtGameTime:N3} / {view.LastFrame}");
        text.AppendLine($"Received UTC: {view.LastReceivedAtUtc:O}");
        text.AppendLine();

        if (view.Map is not null)
        {
            text.AppendLine($"Map: {view.Map.Name} ({view.Map.SizeMeters:N0} m)");
            text.AppendLine($"Map state: {Describe(view.Map.Metadata)}");
        }
        if (view.Player is not null)
        {
            text.AppendLine($"Player: {view.Player.Side} at {Position(view.Player.Metadata.Position)}");
            text.AppendLine($"Player state: {Describe(view.Player.Metadata)}");
        }
        if (view.Group is not null)
        {
            string label = string.IsNullOrWhiteSpace(view.Group.LocalLabel) ? "(not supplied)" : view.Group.LocalLabel;
            text.AppendLine($"Group: {label} / {view.Group.Side} ({Describe(view.Group.Metadata)})");
        }
        text.AppendLine(view.Vehicle is null
            ? "Vehicle: on foot"
            : $"Vehicle: {view.Vehicle.DisplayName} / {view.Vehicle.Role} ({Describe(view.Vehicle.Metadata)})");
        text.AppendLine();

        foreach (WorldFreshness freshness in Enum.GetValues<WorldFreshness>())
        {
            int count = view.KnownContacts.Count(contact => contact.Metadata.FreshnessClass == freshness);
            text.AppendLine($"Contacts {freshness.ToString().ToLowerInvariant()}: {count}");
        }
        if (view.KnownContacts.Count > 0)
        {
            text.AppendLine();
            foreach (WorldKnownContactState contact in view.KnownContacts.Take(64))
            {
                text.AppendLine(
                    $"{contact.Alias}: {contact.Class} / {contact.PerceivedSide} / " +
                    $"{Describe(contact.Metadata)} / pos {Position(contact.Metadata.Position)}");
            }
        }

        text.AppendLine();
        text.AppendLine("Identity limits: group identity is label-derived; contact identity source is not declared by telemetry-v1; vehicle identity is the current-slot only.");
        text.AppendLine("Mission limit: telemetry-v1 has no explicit mission ID, so a same-map reset requires clock/frame evidence.");
        return text.ToString();
    }

    private static string Describe(WorldEntityMetadata metadata)
        => $"{metadata.FreshnessClass.ToString().ToLowerInvariant()}, " +
           $"age {metadata.AgeSeconds:N1}s, confidence {metadata.Confidence:N2}, " +
           $"source {metadata.Source.ToString().ToLowerInvariant()}, identity {metadata.IdentityQuality}";

    private static string Position(WorldPosition? position)
        => position is null
            ? "unavailable"
            : string.Create(CultureInfo.InvariantCulture, $"{position.X:N1} / {position.Y:N1} / {position.Z:N1}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _store.StateChanged -= OnStateChanged;
    }
}
