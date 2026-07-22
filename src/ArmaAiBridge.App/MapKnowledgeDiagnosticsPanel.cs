using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class MapKnowledgeDiagnosticsPanel : UserControl, IDisposable
{
    private readonly MapKnowledgeService _service;
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _state = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 18 };
    private readonly TextBox _details = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas")
    };
    private bool _disposed;

    public MapKnowledgeDiagnosticsPanel(MapKnowledgeService service)
    {
        _service = service;
        Content = BuildUi();
        _service.StateChanged += OnStateChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => Refresh(), Dispatcher);
        _timer.Start();
        Refresh();
    }

    private UIElement BuildUi()
    {
        Grid grid = new() { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = "Static Map Knowledge · read-only local cache",
            FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        _state.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(_state, 1); grid.Children.Add(_state);
        _progress.Margin = new Thickness(0, 0, 0, 12); Grid.SetRow(_progress, 2); grid.Children.Add(_progress);
        Border panel = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 81)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = _details
        };
        Grid.SetRow(panel, 3); grid.Children.Add(panel); return grid;
    }

    private void OnStateChanged(MapKnowledgeDiagnostics _) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (_disposed) return;
        MapKnowledgeDiagnostics value = _service.GetDiagnostics();
        _state.Text = $"{value.Readiness.ToString().ToLowerInvariant()} · {value.WorldName} · {value.ProgressPercent:N1}%";
        _state.Foreground = value.Readiness switch
        {
            MapKnowledgeReadiness.Ready => Brushes.LightGreen,
            MapKnowledgeReadiness.Failed or MapKnowledgeReadiness.Stale => Brushes.IndianRed,
            MapKnowledgeReadiness.Indexing or MapKnowledgeReadiness.Partial => Brushes.Goldenrod,
            _ => Brushes.Gray
        };
        _progress.Value = value.ProgressPercent;
        _details.Text = $"""
            Readiness:          {value.Readiness.ToString().ToLowerInvariant()}
            World:              {Display(value.WorldName)}
            Fingerprint:        {Display(value.Fingerprint)}
            Database path:      {Display(value.DatabasePath)}
            Index version:      {value.IndexVersion}
            Progress:           {value.CompletedTiles:N0} / {value.TotalTiles:N0} tiles
            Export active:      {value.ExportActive}
            Pending tile pages: {value.PendingTilePages:N0}

            Named locations:    {value.Locations:N0}
            Terrain samples:    {value.TerrainSamples:N0}
            Buildings:          {value.Buildings:N0}
            Road segments:      {value.RoadSegments:N0}
            Road intersections: {value.RoadIntersections:N0}
            Vegetation/water:   {value.TileSummaries:N0} tiles

            Last error:         {Display(value.LastError)}

            The database path and full fingerprint are local diagnostics only. Raw tile payloads,
            manifest/addon inputs, and the complete index are never included in OpenAI context.
            """;
    }

    private static string Display(string value) => value.Length == 0 ? "—" : value;

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _service.StateChanged -= OnStateChanged;
    }
}
