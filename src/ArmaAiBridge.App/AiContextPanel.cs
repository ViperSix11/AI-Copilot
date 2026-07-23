using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class AiContextPanel : UserControl, IDisposable
{
    private readonly ContextTraceStore _trace;
    private readonly WorldSnapshotBuilder _snapshots;
    private readonly SqliteStateRepository _stateRepository;
    private readonly TextBox _seed = ContextBox();
    private readonly TextBox _plan = ContextBox();
    private readonly TextBox _retrieved = ContextBox();
    private readonly TextBox _transmitted = ContextBox();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private string _lastRendered = string.Empty;
    private bool _disposed;

    public AiContextPanel(
        ContextTraceStore trace,
        WorldSnapshotBuilder snapshots,
        SqliteStateRepository stateRepository)
    {
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        Content = BuildUi();
        _trace.Changed += Trace_Changed;
        Loaded += (_, _) => Refresh();
    }

    private UIElement BuildUi()
    {
        foreach (TextBox box in new[] { _seed, _plan, _retrieved, _transmitted })
            box.Style = Resource<Style>("TerminalTextBoxStyle");
        _status.Foreground = Resource<Brush>("MutedTextBrush");

        Grid grid = new() { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        StackPanel heading = new();
        heading.Children.Add(new TextBlock
        {
            Text = "Context on Demand  /  hierarchical planning and narrow retrieval",
            Style = Resource<Style>("SectionHeaderTextStyle")
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Shows the actual minimal interaction seed, model-selected catalogue requests, compact tool results and final response. Complete databases, canonical player coordinates, private identifiers, credentials and system instructions are never displayed or transmitted.",
            Foreground = Resource<Brush>("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10)
        });
        grid.Children.Add(heading);

        WrapPanel actions = new() { Margin = new Thickness(0, 0, 0, 8) };
        Button refresh = new() { Content = "Refresh trace", MinWidth = 130 };
        refresh.Click += (_, _) => Refresh(force: true);
        actions.Children.Add(refresh);
        Button reset = new()
        {
            Content = "Reset AI Context...",
            MinWidth = 160,
            Margin = new Thickness(8, 0, 0, 0),
            Style = Resource<Style>("DestructiveButtonStyle")
        };
        reset.Click += ResetContext_Click;
        actions.Children.Add(reset);
        _status.Margin = new Thickness(12, 7, 0, 0);
        actions.Children.Add(_status);
        Grid.SetRow(actions, 1);
        grid.Children.Add(actions);

        TabControl tabs = new();
        tabs.Items.Add(new TabItem { Header = "1. Minimal seed", Content = _seed });
        tabs.Items.Add(new TabItem { Header = "2. AI plan", Content = _plan });
        tabs.Items.Add(new TabItem { Header = "3. Retrieved context", Content = _retrieved });
        tabs.Items.Add(new TabItem { Header = "4. Model-visible exchange", Content = _transmitted });
        Border panel = new() { Style = Resource<Style>("TerminalPanelStyle"), Child = tabs };
        Grid.SetRow(panel, 2);
        grid.Children.Add(panel);
        return grid;
    }

    private void Trace_Changed()
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(() => Refresh());
    }

    private void ResetContext_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            Window.GetWindow(this),
            "Delete local AI context and cached State Mirror data? API keys and response settings are preserved. Arma will repopulate current state after the next handshake.",
            "Reset AI Context",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        _stateRepository.ResetCache();
        _snapshots.ResetTacticalContext();
        _trace.Reset();
        _lastRendered = string.Empty;
        foreach (TextBox box in new[] { _seed, _plan, _retrieved, _transmitted })
            box.Text = "Context was reset. Waiting for the next Arma session handshake and assistant interaction.";
        _status.Text = "Local AI context reset.";
    }

    private void Refresh(bool force = false)
    {
        ContextTraceSnapshot trace = _trace.GetSnapshot();
        if (trace.InteractionAlias.Length == 0)
        {
            foreach (TextBox box in new[] { _seed, _plan, _retrieved, _transmitted })
                box.Text = "No context-on-demand interaction has run yet.";
            _status.Text = "Waiting for an assistant interaction.";
            _lastRendered = string.Empty;
            return;
        }

        string plan = BuildPlan(trace);
        string retrieved = trace.ToolCalls.Count == 0
            ? "The model requested no additional context."
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                trace.ToolCalls.Select((item, index) =>
                    $"{index + 1}. {item.Tool}  /  {item.Group}  /  {item.Category}\n" +
                    $"Arguments:\n{item.SafeArguments}\n\nResult ({item.ResultUtf8Bytes:N0} UTF-8 bytes, {item.ElapsedMilliseconds:N0} ms):\n{item.Result}"));
        string transmitted =
            $"INITIAL EVENT OR PLAYER MESSAGE\n\n{trace.Seed}\n\n" +
            $"REQUESTED TOOL RESULTS ONLY\n\n{trace.ModelVisibleContext}\n\n" +
            $"FINAL RESPONSE\n\n{trace.FinalDecision}";
        string identity = trace.Seed + plan + retrieved + transmitted;
        if (force || !string.Equals(identity, _lastRendered, StringComparison.Ordinal))
        {
            _seed.Text = trace.Seed;
            _plan.Text = plan;
            _retrieved.Text = retrieved;
            _transmitted.Text = transmitted;
            _lastRendered = identity;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ContextUsageSample[] fiveMinutes = trace.RecentUsage
            .Where(item => now - item.CompletedAtUtc <= TimeSpan.FromMinutes(5)).ToArray();
        ContextUsageSample[] thirtyMinutes = trace.RecentUsage
            .Where(item => now - item.CompletedAtUtc <= TimeSpan.FromMinutes(30)).ToArray();
        int lastTotal = trace.InputTokens + trace.OutputTokens;
        _status.Text =
            $"{trace.ToolCallCount} context call(s) · {Encoding.UTF8.GetByteCount(trace.ModelVisibleContext):N0} retrieved bytes · " +
            $"last: {lastTotal:N0} tokens ({trace.InputTokens:N0} input / {trace.OutputTokens:N0} output / {trace.ReasoningTokens:N0} reasoning) · " +
            $"five minutes: {fiveMinutes.Sum(Total):N0} · thirty minutes: {thirtyMinutes.Sum(Total):N0}";
    }

    private static string BuildPlan(ContextTraceSnapshot trace)
    {
        StringBuilder text = new();
        text.AppendLine("DISCOVERABLE HIGH-LEVEL GROUPS");
        foreach (string group in trace.AvailableGroups) text.AppendLine($"- {group}");
        text.AppendLine();
        text.AppendLine("MODEL-SELECTED REQUESTS");
        if (trace.ToolCalls.Count == 0) text.AppendLine("No catalogue or context request was made.");
        foreach ((ContextToolTrace call, int index) in trace.ToolCalls.Select((call, index) => (call, index)))
            text.AppendLine($"{index + 1}. {call.Tool}: {call.Group}/{call.Category}");
        return text.ToString().TrimEnd();
    }

    private static int Total(ContextUsageSample sample)
        => sample.InputTokens + sample.OutputTokens;

    private static TextBox ContextBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private static T Resource<T>(string key) where T : class
        => (T)Application.Current.FindResource(key);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trace.Changed -= Trace_Changed;
    }
}
