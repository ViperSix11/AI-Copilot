using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class AiContextPanel : UserControl, IDisposable
{
    private readonly WorldSnapshotBuilder _snapshots;
    private readonly SettingsService _settings;
    private readonly TextBox _question = new() { MinHeight = 44, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _candidates = EvidenceBox();
    private readonly TextBox _selected = EvidenceBox();
    private readonly TextBox _fused = EvidenceBox();
    private readonly TextBox _transmitted = EvidenceBox();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastRendered = string.Empty;

    public AiContextPanel(WorldSnapshotBuilder snapshots, SettingsService settings)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Content = BuildUi();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private UIElement BuildUi()
    {
        foreach (TextBox box in new[] { _candidates, _selected, _fused, _transmitted })
            box.Style = Resource<Style>("TerminalTextBoxStyle");
        _status.Foreground = Resource<Brush>("MutedTextBrush");

        Grid grid = new() { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        StackPanel heading = new();
        heading.Children.Add(new TextBlock
        {
            Text = "AI Context  /  unified evidence selection and fusion",
            Style = Resource<Style>("SectionHeaderTextStyle")
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Inspect all bounded candidates, deterministic selection, fused interpretation, and the exact tactical context supplied with the next question. Player position, private identifiers, credentials, system instructions and conversation history are not displayed.",
            Foreground = Resource<Brush>("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        });
        grid.Children.Add(heading);

        StackPanel previewInput = new();
        previewInput.Children.Add(new TextBlock { Text = "Prospective question or report (controls deterministic evidence selection)" });
        previewInput.Children.Add(_question);
        Grid.SetRow(previewInput, 1);
        grid.Children.Add(previewInput);

        WrapPanel actions = new() { Margin = new Thickness(0, 8, 0, 8) };
        Button refresh = new() { Content = "Refresh evidence pipeline", MinWidth = 170 };
        refresh.Click += (_, _) => Refresh(force: true);
        actions.Children.Add(refresh);
        _status.Margin = new Thickness(12, 7, 0, 0);
        actions.Children.Add(_status);
        Grid.SetRow(actions, 2);
        grid.Children.Add(actions);

        TabControl evidenceTabs = new();
        evidenceTabs.Items.Add(new TabItem { Header = "1. Candidates", Content = _candidates });
        evidenceTabs.Items.Add(new TabItem { Header = "2. Selected", Content = _selected });
        evidenceTabs.Items.Add(new TabItem { Header = "3. Fused", Content = _fused });
        evidenceTabs.Items.Add(new TabItem { Header = "4. Transmitted", Content = _transmitted });
        Border panel = new() { Style = Resource<Style>("TerminalPanelStyle"), Child = evidenceTabs };
        Grid.SetRow(panel, 3);
        grid.Children.Add(panel);
        return grid;
    }

    private async void Refresh(bool force = false)
    {
        string question = _question.Text.Trim();
        if (!_snapshots.TryBuildEvidenceContext(question, out TacticalEvidenceReport report))
        {
            foreach (TextBox box in new[] { _candidates, _selected, _fused, _transmitted })
                box.Text = "No canonical State Mirror snapshot is available yet.";
            _status.Text = "Waiting for Arma state.";
            _lastRendered = string.Empty;
            return;
        }

        string operatorPrompt;
        try
        {
            operatorPrompt = ResponseProfilePolicy.Normalize((await _settings.LoadAsync()).ResponseProfile).OperatorPrePrompt;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            operatorPrompt = "Operator pre-prompt unavailable because settings could not be read.";
        }

        string transmitted = $"OPERATOR PRE-PROMPT — APPLIED BEFORE CONTEXT\n\n{operatorPrompt}\n\n" +
                             $"EXACT SELECTED AND FUSED TACTICAL CONTEXT\n\n{report.ModelContext}\n\n" +
                             "PROSPECTIVE PLAYER INPUT\n\n" +
                             (question.Length == 0 ? "No prospective question entered." : question);
        string identity = $"{report.CandidateEvidence}\n{report.SelectedEvidence}\n{report.FusedInterpretation}\n{transmitted}";
        if (force || !string.Equals(identity, _lastRendered, StringComparison.Ordinal))
        {
            _candidates.Text = report.CandidateEvidence;
            _selected.Text = report.SelectedEvidence;
            _fused.Text = report.FusedInterpretation;
            _transmitted.Text = transmitted;
            _lastRendered = identity;
        }
        _status.Text = $"{report.SelectedCount:N0} of {report.CandidateCount:N0} candidates selected · " +
                       $"{Encoding.UTF8.GetByteCount(report.ModelContext):N0} transmitted context bytes · refreshed {DateTime.Now:T}";
    }

    private static TextBox EvidenceBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private static T Resource<T>(string key) where T : class
        => (T)Application.Current.FindResource(key);

    public void Dispose() => _timer.Stop();
}
