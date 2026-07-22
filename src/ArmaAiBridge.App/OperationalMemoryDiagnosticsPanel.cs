using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class OperationalMemoryDiagnosticsPanel : UserControl, IDisposable
{
    private readonly OperationalMemoryStore _store;
    private readonly DispatcherTimer _timer;
    private readonly TextBox _summary = Box(wrap: true);
    private readonly TextBox _details = Box(wrap: false);
    private bool _disposed;

    public OperationalMemoryDiagnosticsPanel(OperationalMemoryStore store)
    {
        _store = store;
        Content = BuildUi();
        _store.StateChanged += OnChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private UIElement BuildUi()
    {
        Grid grid = new() { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Panel("Observation-derived state", _summary));
        Grid details = Panel("Entities and immutable observations (read only)", _details);
        Grid.SetColumn(details, 2);
        grid.Children.Add(details);
        return grid;
    }

    private static Grid Panel(string title, UIElement content)
    {
        Grid grid = new();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        Border border = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 81)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = content
        };
        Grid.SetRow(border, 1);
        grid.Children.Add(border);
        return grid;
    }

    private static TextBox Box(bool wrap) => new()
    {
        IsReadOnly = true, AcceptsReturn = true,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        BorderThickness = new Thickness(0), Background = Brushes.Transparent
    };

    private void OnChanged()
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (_disposed) return;
        OperationalMemoryView view = _store.GetCurrentView();
        StringBuilder summary = new();
        summary.AppendLine($"Memory: {Text(view.Readiness)} · schema {view.SchemaVersion}");
        summary.AppendLine($"Session: {Value(view.SessionAlias)}");
        summary.AppendLine($"World: {Value(view.WorldName)}");
        summary.AppendLine($"Database: {Value(view.DatabasePath)}");
        summary.AppendLine($"Gazetteer: {Text(view.GazetteerReadiness)} · {view.GazetteerLocationCount} official names · {Value(view.GazetteerFingerprintAlias)}");
        summary.AppendLine($"Entities: {view.Entities.Count} · observations: {view.Observations.Count}");
        summary.AppendLine($"Entity kinds: {Counts(view.Entities.Select(item => item.Kind))}");
        summary.AppendLine($"Freshness: {Counts(view.Entities.Select(item => item.Freshness))}");
        summary.AppendLine($"Provenance: {Counts(view.Observations.Select(item => item.Provenance))}");
        summary.AppendLine($"Conflicted entities: {view.Entities.Count(item => item.ConflictCount > 0)}");
        summary.AppendLine($"Last batch: {Value(view.LastBatchAlias)}");
        summary.AppendLine($"Diagnostic: {Value(view.DiagnosticCode)}");
        summary.AppendLine();
        summary.AppendLine("Only official names are preloaded. Operational objects exist here only after bounded own-side, mission-authorized, or explicit player evidence.");
        _summary.Text = summary.ToString();

        StringBuilder details = new();
        details.AppendLine("ENTITIES");
        foreach (OperationalEntityState entity in view.Entities.Take(100))
            details.AppendLine($"{entity.Alias} · {Text(entity.Kind)} · {entity.Classification} · {Position(entity.Position)} ±{Number(entity.PositionErrorMeters)}m · {entity.State} · {Text(entity.Freshness)} · confidence {entity.Confidence:0.000} · corroboration {entity.CorroborationCount} · conflicts {entity.ConflictCount}{(entity.IsLastKnown ? " · LAST KNOWN" : string.Empty)}{(entity.IsRetracted ? " · RETRACTED" : string.Empty)}");
        if (view.Entities.Count == 0) details.AppendLine("No operational entities learned in the active session.");
        details.AppendLine();
        details.AppendLine("OBSERVATIONS");
        foreach (OperationalObservationState observation in view.Observations.Take(150))
            details.AppendLine($"{observation.Alias} → {observation.EntityAlias} · source {observation.SourceAlias} · {Text(observation.Provenance)} · game {observation.ObservedAtGameTime:0.###} · {Position(observation.Position)} ±{Number(observation.PositionErrorMeters)}m · base {observation.BaseConfidence:0.00} · corroborates [{string.Join(',', observation.Corroborates)}] · contradicts [{string.Join(',', observation.Contradicts)}]{(observation.ConstraintLocationAlias.Length == 0 ? string.Empty : $" · constraint {observation.ConstraintLocationAlias} {Position(observation.ConstraintPosition)} ±{Number(observation.ConstraintRadiusMeters)}m")}{(observation.Supersedes.Length == 0 ? string.Empty : $" · supersedes {observation.Supersedes}")}{(observation.ConstraintConflict ? " · CONSTRAINT CONFLICT" : string.Empty)}{(observation.RetractedAtUtc is null ? string.Empty : " · RETRACTED")}");
        if (view.Observations.Count == 0) details.AppendLine("No observations received.");
        _details.Text = details.ToString();
    }

    private static string Counts<T>(IEnumerable<T> values) where T : struct, Enum
        => string.Join(", ", values.GroupBy(Text).OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Count()}"));
    private static string Position(WorldPosition? position) => position is null ? "unknown" : $"[{position.X:0.#},{position.Y:0.#},{position.Z:0.#}]";
    private static string Number(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?";
    private static string Text<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    private static string Value(string value) => value.Length == 0 ? "unavailable" : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _store.StateChanged -= OnChanged;
    }
}
