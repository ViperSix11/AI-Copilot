using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class AssistantPanel : UserControl, IDisposable
{
    private enum CapturePurpose { MicrophoneTest, TranscriptionTest, AssistantTurn }

    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly ArmaQueryCoordinator _queries;
    private readonly WorldSnapshotBuilder _snapshots;
    private readonly PushToTalkCaptureCoordinator _capture;
    private readonly AssistantTurnService _turns;
    private readonly VoiceInteractionService _voice;
    private readonly TextBox _conversation = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _question = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76 };
    private readonly TextBox _model = new() { Text = "gpt-5-mini", Width = 220 };
    private readonly TextBlock _status = new() { Text = "Ready.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _voiceStage = new() { Text = "Voice: ready", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly Button _ask = new() { Content = "Ask", MinWidth = 100 };
    private readonly Button _testMicrophone = new() { Content = "Test Microphone", MinWidth = 130, ToolTip = "Press and hold to record; release to play locally." };
    private readonly Button _testTranscription = new() { Content = "Test Transcription", MinWidth = 135, ToolTip = "Press and hold to record; release to send only to OpenAI transcription." };
    private readonly Button _voiceTest = new() { Content = "Test Papa Bear Voice", MinWidth = 160 };
    private readonly Button _holdToTalk = new() { Content = "Hold to Talk", MinWidth = 130, ToolTip = "Press and hold while speaking; release to submit." };
    private readonly Button _replay = new() { Content = "Replay Last Answer", MinWidth = 145, IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 100, IsEnabled = false };
    private CancellationTokenSource? _activeRequest;
    private Button? _heldButton;
    private AudioPayload? _lastAnswerAudio;
    private string? _lastAnswerText;
    private bool _disposed;

    public AssistantPanel(
        TelemetryPipeServer pipe,
        SettingsService settings,
        LogService log,
        WorldSnapshotBuilder snapshots)
    {
        _settings = settings;
        _log = log;
        _snapshots = snapshots;
        _queries = new ArmaQueryCoordinator(pipe);
        _capture = new PushToTalkCaptureCoordinator(new WindowsMicrophoneCaptureService());

        OpenAiAssistantService openAi = new();
        _turns = new AssistantTurnService(
            openAi,
            BuildSnapshot,
            LoadOpenAiSettingsAsync,
            ExecuteToolAsync);
        _voice = new VoiceInteractionService(
            new OpenAiSpeechToTextService(),
            _turns,
            new ElevenLabsTextToSpeechService(),
            new WindowsAudioPlaybackService(),
            LoadVoiceSettingsAsync);

        Content = BuildUi();
        _ask.Click += Ask_Click;
        _cancel.Click += Cancel_Click;
        _voiceTest.Click += VoiceTest_Click;
        _replay.Click += Replay_Click;
        AttachHoldAction(_testMicrophone, CapturePurpose.MicrophoneTest);
        AttachHoldAction(_testTranscription, CapturePurpose.TranscriptionTest);
        AttachHoldAction(_holdToTalk, CapturePurpose.AssistantTurn);
    }

    private UIElement BuildUi()
    {
        Grid grid = new() { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "Papa Bear assistant · typed and push-to-talk · current Arma world state",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        Border transcript = Panel(_conversation);
        Grid.SetRow(transcript, 1);
        grid.Children.Add(transcript);

        StackPanel bottom = new() { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(new TextBlock
        {
            Text = "Questions, answers, transcripts and audio are not written to the application log. Hold-to-talk records for at most 15 seconds.",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        bottom.Children.Add(_question);

        WrapPanel assistantActions = new() { Margin = new Thickness(0, 8, 0, 0) };
        assistantActions.Children.Add(_ask);
        assistantActions.Children.Add(_cancel);
        Button clear = new() { Content = "Clear conversation", MinWidth = 140 };
        clear.Click += (_, _) =>
        {
            if (_activeRequest is not null) return;
            _turns.ResetConversation();
            _conversation.Clear();
            _lastAnswerText = null;
            _lastAnswerAudio?.Dispose();
            _lastAnswerAudio = null;
            _replay.IsEnabled = false;
            _status.Text = "Conversation cleared.";
        };
        assistantActions.Children.Add(clear);
        assistantActions.Children.Add(new TextBlock { Text = "Model", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        assistantActions.Children.Add(_model);
        bottom.Children.Add(assistantActions);

        WrapPanel voiceActions = new() { Margin = new Thickness(0, 8, 0, 0) };
        voiceActions.Children.Add(_testMicrophone);
        voiceActions.Children.Add(_testTranscription);
        voiceActions.Children.Add(_voiceTest);
        voiceActions.Children.Add(_holdToTalk);
        voiceActions.Children.Add(_replay);
        bottom.Children.Add(voiceActions);

        _voiceStage.Margin = new Thickness(0, 8, 0, 0);
        bottom.Children.Add(_voiceStage);
        _status.Margin = new Thickness(0, 4, 0, 0);
        bottom.Children.Add(_status);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(bottom);
        return grid;
    }

    private static Border Panel(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(14, 18, 22)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 81)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12),
        Child = child
    };

    private void AttachHoldAction(Button button, CapturePurpose purpose)
    {
        button.PreviewMouseLeftButtonDown += async (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            await BeginCaptureAsync(button, purpose);
        };
        button.PreviewMouseLeftButtonUp += async (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            button.ReleaseMouseCapture();
            await StopCaptureAsync();
        };
    }

    private async Task BeginCaptureAsync(Button button, CapturePurpose purpose)
    {
        if (_activeRequest is not null || _disposed) return;
        try
        {
            _activeRequest = new CancellationTokenSource();
            _heldButton = button;
            button.CaptureMouse();
            SetOperationBusy(true);
            SetVoiceStage(VoiceStage.Recording);
            _status.Text = "Release the button to stop recording.";
            Task<IAudioRecording> recording = _capture.BeginAsync(_activeRequest.Token);
            _ = ObserveCaptureAsync(recording, purpose, _activeRequest);
        }
        catch (Exception exception)
        {
            HandleVoiceFailure(exception);
            FinishOperation();
        }
    }

    private async Task StopCaptureAsync()
    {
        try { await _capture.ReleaseAsync(); }
        catch (Exception exception) { HandleVoiceFailure(exception); }
    }

    private async Task ObserveCaptureAsync(
        Task<IAudioRecording> recordingTask,
        CapturePurpose purpose,
        CancellationTokenSource request)
    {
        try
        {
            await using (IAudioRecording recording = await recordingTask)
            {
                if (!ReferenceEquals(request, _activeRequest)) return;
                request.Token.ThrowIfCancellationRequested();
                Progress<VoiceStage> progress = new(SetVoiceStage);
                switch (purpose)
                {
                    case CapturePurpose.MicrophoneTest:
                        await _voice.PlayMicrophoneTestAsync(recording, progress, request.Token);
                        _status.Text = "Microphone test completed locally. No provider was contacted.";
                        break;
                    case CapturePurpose.TranscriptionTest:
                        string transcript = await _voice.RunTranscriptionTestAsync(recording, progress, request.Token);
                        Append("Transcription test", transcript);
                        _status.Text = "OpenAI transcription completed. OpenAI Responses and ElevenLabs were not contacted.";
                        break;
                    case CapturePurpose.AssistantTurn:
                        VoiceTurnResult result = await _voice.RunVoiceTurnAsync(
                            recording,
                            progress,
                            transcript => Dispatcher.Invoke(() => Append("You", transcript)),
                            answer => Dispatcher.Invoke(() => CommitVisibleAnswer(answer.Text)),
                            request.Token);
                        if (result.Audio is not null) ReplaceLastAudio(result.Audio);
                        if (result.SpeechFailure is null)
                        {
                            _status.Text = FormatCompletion(result.Answer);
                        }
                        else
                        {
                            _status.Text = SpeechServiceException.FormatPartialSuccessForUser(result.SpeechFailure);
                            _log.Warn(SpeechServiceException.FormatForLog(result.SpeechFailure));
                        }
                        _log.Info($"Spoken assistant text completed: model={result.Answer.Model}, tools={result.Answer.ToolCalls}, inputTokens={result.Answer.InputTokens}, outputTokens={result.Answer.OutputTokens}.");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetVoiceStage(VoiceStage.Ready);
            _status.Text = "Voice operation cancelled.";
        }
        catch (Exception exception)
        {
            HandleVoiceFailure(exception);
        }
        finally
        {
            if (ReferenceEquals(request, _activeRequest)) FinishOperation();
        }
    }

    private async void Ask_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRequest is not null) return;
        string question = _question.Text.Trim();
        if (question.Length == 0)
        {
            _status.Text = "Enter a question first.";
            return;
        }

        try
        {
            _activeRequest = new CancellationTokenSource();
            SetOperationBusy(true);
            SetVoiceStage(VoiceStage.Thinking);
            Append("You", question);
            _question.Clear();
            AssistantResponse response = await _turns.SubmitUserTurnAsync(
                question,
                UserTurnSource.Typed,
                _activeRequest.Token);
            Append("Papa Bear", response.Text);
            _status.Text = FormatCompletion(response);
            SetVoiceStage(VoiceStage.Ready);
            _log.Info($"Typed assistant turn completed: model={response.Model}, tools={response.ToolCalls}, inputTokens={response.InputTokens}, outputTokens={response.OutputTokens}.");
        }
        catch (OperationCanceledException)
        {
            SetVoiceStage(VoiceStage.Ready);
            _status.Text = "Assistant turn cancelled.";
        }
        catch (Exception exception)
        {
            HandleAssistantFailure(exception);
        }
        finally
        {
            FinishOperation();
        }
    }

    private async void VoiceTest_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRequest is not null) return;
        try
        {
            _activeRequest = new CancellationTokenSource();
            SetOperationBusy(true);
            Progress<VoiceStage> progress = new(SetVoiceStage);
            using AudioPayload audio = await _voice.RunVoiceTestAsync(progress, _activeRequest.Token);
            _status.Text = $"Played: “{VoiceInteractionService.VoiceTestPhrase}”";
        }
        catch (OperationCanceledException)
        {
            SetVoiceStage(VoiceStage.Ready);
            _status.Text = "Voice test cancelled.";
        }
        catch (Exception exception)
        {
            HandleVoiceFailure(exception);
        }
        finally
        {
            FinishOperation();
        }
    }

    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRequest is not null || string.IsNullOrWhiteSpace(_lastAnswerText)) return;
        try
        {
            _activeRequest = new CancellationTokenSource();
            SetOperationBusy(true);
            Progress<VoiceStage> progress = new(SetVoiceStage);
            SpeechOutputResult result = await _voice.RetrySpeechAsync(
                _lastAnswerText,
                _lastAnswerAudio,
                progress,
                _activeRequest.Token);
            if (result.Audio is not null) ReplaceLastAudio(result.Audio);
            if (result.Failure is null)
            {
                _status.Text = "Last text answer spoken successfully.";
            }
            else
            {
                _status.Text = SpeechServiceException.FormatPartialSuccessForUser(result.Failure);
                _log.Warn(SpeechServiceException.FormatForLog(result.Failure));
            }
        }
        catch (OperationCanceledException)
        {
            SetVoiceStage(VoiceStage.Ready);
            _status.Text = "Replay cancelled.";
        }
        catch (Exception exception)
        {
            HandleVoiceFailure(exception);
        }
        finally
        {
            FinishOperation();
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? request = _activeRequest;
        if (request is null) return;
        request.Cancel();
        _voice.StopPlayback();
        try { await _capture.ReleaseAsync(); }
        catch { }
    }

    private (bool Success, string Snapshot) BuildSnapshot()
        => _snapshots.TryBuildCurrentSituation(out string snapshot)
            ? (true, snapshot)
            : (false, string.Empty);

    private async Task<(string ApiKey, string Model)> LoadOpenAiSettingsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await _settings.LoadAsync(cancellationToken);
        string model = Dispatcher.CheckAccess() ? _model.Text.Trim() : Dispatcher.Invoke(() => _model.Text.Trim());
        return (DpapiService.Unprotect(settings.OpenAiApiKeyProtected), model);
    }

    private async Task<VoiceProviderSettings> LoadVoiceSettingsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await _settings.LoadAsync(cancellationToken);
        return new VoiceProviderSettings(
            DpapiService.Unprotect(settings.OpenAiApiKeyProtected),
            DpapiService.Unprotect(settings.ElevenLabsApiKeyProtected),
            settings.ElevenLabsVoiceId.Trim());
    }

    private Task<string> ExecuteToolAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken)
        => name switch
        {
            "query_environment" => _queries.QueryEnvironmentAsync(arguments, cancellationToken),
            "query_friendly_forces" => Task.FromResult(_snapshots.BuildFriendlyForces(arguments)),
            "query_assets" => Task.FromResult(_snapshots.BuildAssets(arguments)),
            "query_mission_capabilities" => Task.FromResult(_snapshots.BuildMissionCapabilities(arguments)),
            _ => Task.FromException<string>(new InvalidOperationException("Unsupported local tool."))
        };

    private void Append(string speaker, string text)
    {
        if (_conversation.Text.Length > 0) _conversation.AppendText(Environment.NewLine + Environment.NewLine);
        _conversation.AppendText($"{speaker}: {text}");
        _conversation.ScrollToEnd();
    }

    private void ReplaceLastAudio(AudioPayload audio)
    {
        if (ReferenceEquals(_lastAnswerAudio, audio)) return;
        AudioPayload? previous = _lastAnswerAudio;
        _lastAnswerAudio = audio;
        previous?.Dispose();
        _replay.IsEnabled = _activeRequest is null && !string.IsNullOrWhiteSpace(_lastAnswerText);
    }

    private void CommitVisibleAnswer(string text)
    {
        Append("Papa Bear", text);
        _lastAnswerText = text;
        _lastAnswerAudio?.Dispose();
        _lastAnswerAudio = null;
    }

    private void SetVoiceStage(VoiceStage stage)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetVoiceStage(stage));
            return;
        }
        string value = stage switch
        {
            VoiceStage.GeneratingVoice => "generating-voice",
            _ => stage.ToString().ToLowerInvariant()
        };
        _voiceStage.Text = $"Voice: {value}";
    }

    private void SetOperationBusy(bool busy)
    {
        _ask.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
        _question.IsEnabled = !busy;
        _model.IsEnabled = !busy;
        _testMicrophone.IsEnabled = !busy;
        _testTranscription.IsEnabled = !busy;
        _voiceTest.IsEnabled = !busy;
        _holdToTalk.IsEnabled = !busy;
        if (busy && _heldButton is not null) _heldButton.IsEnabled = true;
        _replay.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_lastAnswerText);
    }

    private void FinishOperation()
    {
        _heldButton?.ReleaseMouseCapture();
        _heldButton = null;
        _activeRequest?.Dispose();
        _activeRequest = null;
        SetOperationBusy(false);
    }

    private void HandleAssistantFailure(Exception exception)
    {
        SetVoiceStage(VoiceStage.Failed);
        _status.Text = exception.Message;
        _log.Warn(OpenAiAssistantService.FormatFailureForLog(exception));
    }

    private void HandleVoiceFailure(Exception exception)
    {
        SetVoiceStage(VoiceStage.Failed);
        _status.Text = exception.Message;
        _log.Warn(SpeechServiceException.FormatForLog(exception));
    }

    private static string FormatCompletion(AssistantResponse response)
        => $"{response.Model} · {response.ToolCalls} tool call(s) · {response.InputTokens} input / {response.OutputTokens} output tokens";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeRequest?.Cancel();
        _voice.StopPlayback();
        _ = _capture.ReleaseAsync();
        _lastAnswerAudio?.Dispose();
        _lastAnswerText = null;
        _voice.Dispose();
        _turns.Dispose();
        _queries.Dispose();
        _ = _capture.DisposeAsync();
        _activeRequest?.Dispose();
    }
}
