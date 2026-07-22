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

        if (view.Protocol is not null)
        {
            text.AppendLine($"Protocol: {view.Protocol.Major}.{view.Protocol.Minor} / {view.Protocol.ViewerSide} / {view.Protocol.Visibility}");
            text.AppendLine($"Features: {string.Join(", ", view.Protocol.Features.Select(item => $"{item.Name}@{item.Version}"))}");
        }
        else
        {
            text.AppendLine("Protocol: legacy telemetry (no handshake)");
        }
        WorldReconciliationState reconciliation = view.Reconciliation;
        text.AppendLine(
            $"Reconciliation: {(reconciliation.IsDegraded ? "degraded" : "healthy")} / " +
            $"complete={reconciliation.HasCompleteReconciliation} / sequence={reconciliation.LastSequence} / " +
            $"gap={reconciliation.SequenceGap} / pendingPages={reconciliation.PendingPageCount}");
        text.AppendLine(
            $"Last full reconciliation: " +
            $"{(reconciliation.LastReconciliationId.Length == 0 ? "none" : reconciliation.LastReconciliationId)} / " +
            $"{(reconciliation.LastReconciledAtUtc is null ? "never" : reconciliation.LastReconciledAtUtc.Value.ToString("O", CultureInfo.InvariantCulture))}");
        text.AppendLine($"Capability registry: v{reconciliation.CapabilityRegistryVersion}");
        if (reconciliation.DiagnosticCode.Length > 0)
            text.AppendLine($"Reconciliation diagnostic: {reconciliation.DiagnosticCode}");
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

        text.AppendLine($"Friendly groups: {FreshnessCounts(view.FriendlyGroups.Select(item => item.Metadata))}");
        foreach (WorldFriendlyGroupState group in view.FriendlyGroups.Take(64))
        {
            text.AppendLine(
                $"  {group.Alias} / {group.Callsign}: {group.UnitAliases.Count} units / " +
                $"{Describe(group.Metadata)} / pos {Position(group.Metadata.Position)}");
        }
        text.AppendLine($"Friendly units: {FreshnessCounts(view.FriendlyUnits.Select(item => item.Metadata))}");
        foreach (WorldFriendlyUnitState unit in view.FriendlyUnits.Take(128))
        {
            text.AppendLine(
                $"  {unit.Alias} / {unit.Callsign}: {unit.Role} / {unit.LifeState} / " +
                $"mobile={unit.Mobile} / {Describe(unit.Metadata)} / pos {Position(unit.Metadata.Position)}");
        }
        text.AppendLine($"Friendly vehicles: {FreshnessCounts(view.FriendlyVehicles.Select(item => item.Metadata))}");
        foreach (WorldFriendlyVehicleState vehicle in view.FriendlyVehicles.Take(64))
        {
            text.AppendLine(
                $"  {vehicle.Alias}: {vehicle.DisplayName} / mobile={vehicle.Mobile} / " +
                $"fuel={vehicle.Fuel:N2} / {Describe(vehicle.Metadata)} / pos {Position(vehicle.Metadata.Position)}");
        }
        text.AppendLine($"Support assets: {FreshnessCounts(view.SupportAssets.Select(item => item.Metadata))}");
        foreach (WorldSupportAssetState asset in view.SupportAssets.Take(64))
        {
            text.AppendLine(
                $"  {asset.Alias} / {asset.Callsign}: {asset.Kind} / {asset.Status} / " +
                $"available={asset.Available} / {Describe(asset.Metadata)}");
        }
        text.AppendLine($"Mission capabilities: {FreshnessCounts(view.Capabilities.Select(item => item.Metadata))}");
        foreach (WorldCapabilityState capability in view.Capabilities.Take(64))
        {
            text.AppendLine(
                $"  {capability.Alias}: {capability.Capability} / enabled={capability.Enabled} / " +
                $"provider={capability.Provider} / {Describe(capability.Metadata)}");
        }
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
        text.AppendLine("Identity limits: raw engine IDs stay private store keys; diagnostics uses session aliases. Legacy current-group identity remains label-derived and current vehicle remains a slot.");
        text.AppendLine(view.Protocol is null
            ? "Mission limit: legacy telemetry has no explicit mission ID, so a same-map reset requires clock/frame evidence."
            : "Mission lifecycle: the protocol handshake is authoritative; force identities are scoped to this local session.");
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

    private static string FreshnessCounts(IEnumerable<WorldEntityMetadata> metadata)
    {
        WorldEntityMetadata[] items = metadata.ToArray();
        return $"{items.Length} total / " + string.Join(", ", Enum.GetValues<WorldFreshness>()
            .Select(freshness =>
                $"{freshness.ToString().ToLowerInvariant()}={items.Count(item => item.FreshnessClass == freshness)}"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _store.StateChanged -= OnStateChanged;
    }
}
