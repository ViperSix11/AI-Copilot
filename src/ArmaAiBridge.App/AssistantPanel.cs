using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class AssistantPanel : UserControl, IDisposable
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly ArmaQueryCoordinator _queries;
    private readonly OpenAiAssistantService _assistant = new();
    private readonly TextBox _conversation = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _question = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76 };
    private readonly TextBox _model = new() { Text = "gpt-5-mini", Width = 220 };
    private readonly TextBlock _status = new() { Text = "Ready.", TextWrapping = TextWrapping.Wrap };
    private readonly Button _ask = new() { Content = "Ask", MinWidth = 100 };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 100, IsEnabled = false };
    private CancellationTokenSource? _activeRequest;
    private bool _disposed;

    public AssistantPanel(TelemetryPipeServer pipe, SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
        _queries = new ArmaQueryCoordinator(pipe);
        Content = BuildUi();
        _ask.Click += Ask_Click;
        _cancel.Click += (_, _) => _activeRequest?.Cancel();
    }

    private UIElement BuildUi()
    {
        Grid grid = new() { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "Text assistant · OpenAI Responses API · live Arma tool calls",
            FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        Border transcript = Panel(_conversation); Grid.SetRow(transcript, 1); grid.Children.Add(transcript);

        StackPanel bottom = new() { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(new TextBlock
        {
            Text = "Questions and answers are not written to the application log. Player name, UID, group ID and object IDs are removed before API requests.",
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        bottom.Children.Add(_question);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(_ask); actions.Children.Add(_cancel);
        Button clear = new() { Content = "Clear conversation", MinWidth = 140 };
        clear.Click += (_, _) => { _assistant.ResetConversation(); _conversation.Clear(); _status.Text = "Conversation cleared."; };
        actions.Children.Add(clear);
        actions.Children.Add(new TextBlock { Text = "Model", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        actions.Children.Add(_model);
        bottom.Children.Add(actions);
        _status.Margin = new Thickness(0, 8, 0, 0); bottom.Children.Add(_status);
        Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        return grid;
    }

    private static Border Panel(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(14, 18, 22)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 81)), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Child = child
    };

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRequest is not null) return;
        string question = _question.Text.Trim();
        if (question.Length == 0) { _status.Text = "Enter a question first."; return; }
        if (!_queries.TryGetLatestTelemetry(out string telemetry)) { _status.Text = "No live Arma telemetry is available yet."; return; }

        try
        {
            AppSettings settings = await _settings.LoadAsync();
            string apiKey = DpapiService.Unprotect(settings.OpenAiApiKeyProtected);
            _activeRequest = new CancellationTokenSource();
            SetBusy(true); Append("You", question); _question.Clear();
            AssistantResponse response = await _assistant.AskAsync(
                apiKey, _model.Text.Trim(), question, telemetry, _queries.QueryEnvironmentAsync, _activeRequest.Token);
            Append("Bridge", response.Text);
            _status.Text = $"{response.Model} · {response.ToolCalls} tool call(s) · {response.InputTokens} input / {response.OutputTokens} output tokens";
            _log.Info($"OpenAI assistant completed: model={response.Model}, tools={response.ToolCalls}, inputTokens={response.InputTokens}, outputTokens={response.OutputTokens}.");
        }
        catch (OperationCanceledException) { _status.Text = "Request cancelled."; }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _log.Warn(OpenAiAssistantService.FormatFailureForLog(ex));
        }
        finally
        {
            _activeRequest?.Dispose(); _activeRequest = null; SetBusy(false);
        }
    }

    private void Append(string speaker, string text)
    {
        if (_conversation.Text.Length > 0) _conversation.AppendText(Environment.NewLine + Environment.NewLine);
        _conversation.AppendText($"{speaker}: {text}"); _conversation.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        _ask.IsEnabled = !busy; _cancel.IsEnabled = busy; _question.IsEnabled = !busy; _model.IsEnabled = !busy;
        if (busy) _status.Text = "Reasoning…";
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _activeRequest?.Cancel(); _activeRequest?.Dispose(); _queries.Dispose(); _assistant.Dispose();
    }
}
