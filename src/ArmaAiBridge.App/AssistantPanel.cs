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
    private readonly CheckBox _globalPttEnabled = new() { Content = "Enable global push-to-talk" };
    private readonly TextBlock _globalPttHotkey = new() { Text = "Hotkey: Shift + Space" };
    private readonly TextBlock _globalPttStatus = new() { Text = "Status: Initializing", TextWrapping = TextWrapping.Wrap };
    private readonly Button _changeGlobalPtt = new() { Content = "Change hotkey", MinWidth = 120 };
    private readonly Button _resetGlobalPtt = new() { Content = "Reset to default", MinWidth = 120 };
    private CancellationTokenSource? _activeRequest;
    private Button? _heldButton;
    private AudioPayload? _lastAnswerAudio;
    private string? _lastAnswerText;
    private string? _lastAnswerSpeechText;
    private GlobalPushToTalkController? _globalPtt;
    private GlobalPushToTalkHotkey _globalPttBinding = GlobalPushToTalkHotkey.Default;
    private bool _capturingGlobalHotkey;
    private bool _globalPttLoaded;
    private bool _updatingGlobalPttUi;
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
            ExecuteToolAsync,
            _snapshots.GetCurrentGroupCallsign,
            ballisticProfileFactory: _snapshots.GetCurrentBallisticProfile,
            contextualExecuteTool: ExecuteToolAsync);
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
        Loaded += AssistantPanel_Loaded;
        PreviewKeyDown += AssistantPanel_PreviewKeyDown;
        _globalPttEnabled.Checked += GlobalPttEnabled_Changed;
        _globalPttEnabled.Unchecked += GlobalPttEnabled_Changed;
        _changeGlobalPtt.Click += ChangeGlobalPtt_Click;
        _resetGlobalPtt.Click += ResetGlobalPtt_Click;
    }

    private UIElement BuildUi()
    {
        _conversation.Style = Resource<Style>("TerminalTextBoxStyle");
        _question.MinHeight = 90;
        _voiceStage.Foreground = Resource<Brush>("AccentStrongBrush");
        _status.Foreground = Resource<Brush>("MutedTextBrush");
        _cancel.Style = Resource<Style>("DestructiveButtonStyle");

        Grid grid = new() { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TextBlock heading = new()
        {
            Text = "Papa Bear assistant  /  typed and push-to-talk  /  current Arma world state",
            Margin = new Thickness(0, 0, 0, 12),
            Style = Resource<Style>("SectionHeaderTextStyle")
        };
        grid.Children.Add(heading);
        Border transcript = Panel(_conversation);
        Grid.SetRow(transcript, 1);
        grid.Children.Add(transcript);

        StackPanel bottom = new() { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(new TextBlock
        {
            Text = "Questions, answers, transcripts and audio are not written to the application log. Hold-to-talk records for at most 15 seconds.",
            Foreground = Resource<Brush>("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        bottom.Children.Add(_question);

        WrapPanel assistantActions = new() { Margin = new Thickness(0, 8, 0, 0) };
        assistantActions.Children.Add(_ask);
        assistantActions.Children.Add(_cancel);
        Button clear = new() { Content = "Clear conversation", MinWidth = 140 };
        clear.Style = Resource<Style>("DestructiveButtonStyle");
        clear.Click += (_, _) =>
        {
            if (_activeRequest is not null) return;
            _turns.ResetConversation();
            _conversation.Clear();
            _lastAnswerText = null;
            _lastAnswerSpeechText = null;
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

        StackPanel globalPtt = new() { Margin = new Thickness(0, 12, 0, 0) };
        globalPtt.Children.Add(new TextBlock
        {
            Text = "Global push-to-talk",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        globalPtt.Children.Add(_globalPttEnabled);
        globalPtt.Children.Add(_globalPttHotkey);
        WrapPanel globalActions = new() { Margin = new Thickness(0, 4, 0, 0) };
        globalActions.Children.Add(_changeGlobalPtt);
        globalActions.Children.Add(_resetGlobalPtt);
        globalPtt.Children.Add(globalActions);
        globalPtt.Children.Add(_globalPttStatus);
        globalPtt.Children.Add(new TextBlock
        {
            Text = "The registered combination is reserved globally while the Bridge is running.",
            Foreground = Resource<Brush>("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        bottom.Children.Add(globalPtt);

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
        Style = Resource<Style>("TerminalPanelStyle"),
        Child = child
    };

    private static T Resource<T>(string key) where T : class
        => (T)Application.Current.FindResource(key);

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

    private async Task<bool> BeginCaptureAsync(Button? button, CapturePurpose purpose)
    {
        if (_activeRequest is not null || _disposed) return false;
        try
        {
            _activeRequest = new CancellationTokenSource();
            _heldButton = button;
            button?.CaptureMouse();
            SetOperationBusy(true);
            SetVoiceStage(VoiceStage.Recording);
            _status.Text = "Release the button to stop recording.";
            Task<IAudioRecording> recording = _capture.BeginAsync(_activeRequest.Token);
            _ = ObserveCaptureAsync(recording, purpose, _activeRequest);
            return true;
        }
        catch (Exception exception)
        {
            HandleVoiceFailure(exception);
            FinishOperation();
            return false;
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
                if (purpose == CapturePurpose.AssistantTurn && !PushToTalkRecordingPolicy.ShouldSubmit(recording.Duration))
                {
                    SetVoiceStage(VoiceStage.Ready);
                    _status.Text = "Ready.";
                    _log.Info($"Push-to-talk recording cancelled: durationMs={(long)recording.Duration.TotalMilliseconds}, cancelledAsShortPress=true.");
                    return;
                }
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
                            acknowledgement => Dispatcher.Invoke(() => ShowAcknowledgement(acknowledgement)),
                            answer => Dispatcher.Invoke(() => CommitVisibleAnswer(answer)),
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
                        LogCompletion("Spoken", result.Answer);
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
                (acknowledgement, _) =>
                {
                    Dispatcher.Invoke(() => ShowAcknowledgement(acknowledgement));
                    return Task.CompletedTask;
                },
                response => Dispatcher.Invoke(() => CommitVisibleAnswer(response)),
                _activeRequest.Token);
            _status.Text = FormatCompletion(response);
            SetVoiceStage(VoiceStage.Ready);
            LogCompletion("Typed", response);
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
        if (_activeRequest is not null || string.IsNullOrWhiteSpace(_lastAnswerSpeechText)) return;
        try
        {
            _activeRequest = new CancellationTokenSource();
            SetOperationBusy(true);
            Progress<VoiceStage> progress = new(SetVoiceStage);
            SpeechOutputResult result = await _voice.RetrySpeechAsync(
                _lastAnswerSpeechText,
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

    private (bool Success, string Snapshot) BuildSnapshot(string question)
        => _snapshots.TryBuildCurrentSituation(question, out string snapshot)
            ? (true, snapshot)
            : (false, string.Empty);

    private async Task<(string ApiKey, string Model, ResponseProfileSettings Profile)> LoadOpenAiSettingsAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await _settings.LoadAsync(cancellationToken);
        string model = Dispatcher.CheckAccess() ? _model.Text.Trim() : Dispatcher.Invoke(() => _model.Text.Trim());
        return (DpapiService.Unprotect(settings.OpenAiApiKeyProtected), model,
            ResponseProfilePolicy.Normalize(settings.ResponseProfile));
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
            "find_named_locations" => Task.FromResult(_snapshots.BuildNamedLocations(arguments)),
            "query_state" => Task.FromResult(_snapshots.BuildState(arguments)),
            _ => Task.FromException<string>(new InvalidOperationException("Unsupported local tool."))
        };

    private Task<string> ExecuteToolAsync(
        string name,
        JsonElement arguments,
        AssistantToolContext context,
        CancellationToken cancellationToken)
        => name switch
        {
            "calculate_firing_solution" => new BallisticToolService(_queries.QueryTerrainHeightAslAsync)
                .CalculateAsync(arguments, context.BallisticProfile, cancellationToken),
            _ => ExecuteToolAsync(name, arguments, cancellationToken)
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

    private void ShowAcknowledgement(RadioAcknowledgement acknowledgement)
    {
        Append("Papa Bear", acknowledgement.VisibleText);
        _status.Text = "Papa Bear is working. Stand by.";
        SetVoiceStage(VoiceStage.Thinking);
    }

    private void CommitVisibleAnswer(AssistantResponse response)
    {
        Append("Papa Bear", response.Text);
        _lastAnswerText = response.Text;
        _lastAnswerSpeechText = RadioSpeechTextNormalizer.Normalize(
            response.Text,
            response.GroupCallsign);
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
        _changeGlobalPtt.IsEnabled = !busy || _globalPtt?.IsRecording == true;
        _resetGlobalPtt.IsEnabled = !busy || _globalPtt?.IsRecording == true;
    }

    private void FinishOperation()
    {
        _heldButton?.ReleaseMouseCapture();
        _heldButton = null;
        _activeRequest?.Dispose();
        _activeRequest = null;
        SetOperationBusy(false);
    }

    private async void AssistantPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_globalPttLoaded || _disposed) return;
        _globalPttLoaded = true;
        try
        {
            AppSettings settings = await _settings.LoadAsync();
            _globalPttBinding = settings.GlobalPushToTalkHotkey ?? GlobalPushToTalkHotkey.Default;
            try { _globalPttBinding.Validate(); }
            catch { _globalPttBinding = GlobalPushToTalkHotkey.Default; }
            Window window = Window.GetWindow(this) ?? throw new InvalidOperationException("The application window is unavailable.");
            _globalPtt = new GlobalPushToTalkController(
                new WindowsGlobalHotkeyService(window),
                new WindowsKeyStateService(),
                BeginGlobalPushToTalkAsync,
                _ => _capture.ReleaseAsync());
            _globalPtt.StatusChanged += GlobalPtt_StatusChanged;
            SetGlobalPttChecked(_globalPttBinding.Enabled);
            UpdateGlobalPttBindingText();
            GlobalHotkeyRegistrationResult result = _globalPtt.Configure(_globalPttBinding);
            UpdateGlobalPttStatus(result);
        }
        catch (Exception exception)
        {
            _globalPttStatus.Text = "Status: Registration failed";
            _log.Warn($"Global PTT initialization failed: {exception.GetType().Name}.");
        }
    }

    private Task<bool> BeginGlobalPushToTalkAsync(GlobalPushToTalkHotkey binding, CancellationToken cancellationToken)
    {
        if (Dispatcher.CheckAccess()) return BeginGlobalPushToTalkOnUiAsync();
        return Dispatcher.InvokeAsync(BeginGlobalPushToTalkOnUiAsync).Task.Unwrap();

        async Task<bool> BeginGlobalPushToTalkOnUiAsync()
        {
            if (_activeRequest is not null)
            {
                _status.Text = "Voice operation already active.";
                return false;
            }
            _log.Info("Global PTT activated: hotkeyActivationSource=registered-hotkey.");
            return await BeginCaptureAsync(null, CapturePurpose.AssistantTurn);
        }
    }

    private void GlobalPtt_StatusChanged(object? sender, GlobalHotkeyRegistrationResult result)
    {
        if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(() => UpdateGlobalPttStatus(result)); return; }
        UpdateGlobalPttStatus(result);
    }

    private void UpdateGlobalPttStatus(GlobalHotkeyRegistrationResult result)
    {
        _globalPttStatus.Text = $"Status: {result.Message}";
        _log.Info($"Global PTT status: enabled={_globalPttBinding.Enabled}, registered={result.Registered}, result={result.Code}.");
    }

    private void UpdateGlobalPttBindingText() => _globalPttHotkey.Text = $"Hotkey: {_globalPttBinding.DisplayName}";

    private async void GlobalPttEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingGlobalPttUi || !_globalPttLoaded || _globalPtt is null) return;
        await SaveAndApplyGlobalPttAsync(
            _globalPttBinding with { Enabled = _globalPttEnabled.IsChecked == true });
    }

    private void ChangeGlobalPtt_Click(object sender, RoutedEventArgs e)
    {
        if (_globalPtt is null || (_activeRequest is not null && !_globalPtt.IsRecording)) return;
        _capturingGlobalHotkey = true;
        _globalPtt.Suspend();
        _globalPttStatus.Text = "Status: Press the desired key combination… (Escape cancels)";
        Focus();
        Keyboard.Focus(this);
    }

    private async void ResetGlobalPtt_Click(object sender, RoutedEventArgs e)
    {
        if (_globalPtt is null) return;
        _capturingGlobalHotkey = false;
        SetGlobalPttChecked(true);
        await SaveAndApplyGlobalPttAsync(GlobalPushToTalkHotkey.Default);
    }

    private async void AssistantPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingGlobalHotkey || _globalPtt is null) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _capturingGlobalHotkey = false;
            UpdateGlobalPttStatus(_globalPtt.Resume());
            return;
        }
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        GlobalHotkeyModifiers modifiers = GlobalHotkeyModifiers.None;
        ModifierKeys active = Keyboard.Modifiers;
        if (active.HasFlag(ModifierKeys.Shift)) modifiers |= GlobalHotkeyModifiers.Shift;
        if (active.HasFlag(ModifierKeys.Control)) modifiers |= GlobalHotkeyModifiers.Control;
        if (active.HasFlag(ModifierKeys.Alt)) modifiers |= GlobalHotkeyModifiers.Alt;
        if (active.HasFlag(ModifierKeys.Windows)) modifiers |= GlobalHotkeyModifiers.Windows;
        GlobalPushToTalkHotkey candidate = new(true, modifiers, KeyInterop.VirtualKeyFromKey(key));
        try { candidate.Validate(); }
        catch (InvalidOperationException exception)
        {
            _capturingGlobalHotkey = false;
            _globalPtt.Resume();
            _globalPttStatus.Text = $"Status: {exception.Message}";
            return;
        }
        _capturingGlobalHotkey = false;
        SetGlobalPttChecked(true);
        await SaveAndApplyGlobalPttAsync(candidate);
    }

    private async Task SaveAndApplyGlobalPttAsync(GlobalPushToTalkHotkey binding)
    {
        try
        {
            AppSettings settings = await _settings.LoadAsync();
            settings.GlobalPushToTalkHotkey = binding;
            await _settings.SaveAsync(settings);
            _globalPttBinding = binding;
            SetGlobalPttChecked(binding.Enabled);
            UpdateGlobalPttBindingText();
            if (_globalPtt is not null) UpdateGlobalPttStatus(_globalPtt.Configure(binding));
        }
        catch (Exception exception)
        {
            if (_globalPtt is not null) _globalPtt.Resume();
            SetGlobalPttChecked(_globalPttBinding.Enabled);
            _globalPttStatus.Text = "Status: Could not save the hotkey setting.";
            _log.Warn($"Global PTT setting save failed: {exception.GetType().Name}.");
        }
    }

    private void SetGlobalPttChecked(bool value)
    {
        _updatingGlobalPttUi = true;
        try { _globalPttEnabled.IsChecked = value; }
        finally { _updatingGlobalPttUi = false; }
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

    private void LogCompletion(string source, AssistantResponse response)
    {
        AssistantRequestMetrics? metrics = response.RequestMetrics;
        string requestMetrics = metrics is null
            ? string.Empty
            : $", snapshotBytes={metrics.SnapshotUtf8Bytes}, sectionCounts={string.Join("|", metrics.SectionRecordCounts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}:{item.Value}"))}, historyMessages={metrics.HistoryMessageCount}, historyCharacters={metrics.HistoryCharacterCount}, selectedTools={metrics.SelectedToolCount}, acknowledgementEligible={metrics.AcknowledgementEligible}, acknowledgementEmitted={metrics.AcknowledgementEmitted}, acknowledgementThresholdMs={metrics.AcknowledgementThresholdMilliseconds}, acknowledgementVariation={metrics.AcknowledgementVariationId}, answerTextLatencyMs={metrics.AnswerTextLatencyMilliseconds}, responseLatencyMs={metrics.ResponseLatencyMilliseconds}, retryPerformed={metrics.RetryPerformed}, initialIncompleteReason={metrics.InitialIncompleteReason}";
        _log.Info($"{source} assistant text completed: model={response.Model}, tools={response.ToolCalls}, inputTokens={response.InputTokens}, outputTokens={response.OutputTokens}, reasoningTokens={response.ReasoningTokens}{requestMetrics}.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeRequest?.Cancel();
        _voice.StopPlayback();
        _ = _capture.ReleaseAsync();
        _lastAnswerAudio?.Dispose();
        _lastAnswerText = null;
        _lastAnswerSpeechText = null;
        if (_globalPtt is not null)
        {
            _globalPtt.StatusChanged -= GlobalPtt_StatusChanged;
            _globalPtt.Dispose();
            _globalPtt = null;
        }
        _voice.Dispose();
        _turns.Dispose();
        _queries.Dispose();
        _ = _capture.DisposeAsync();
        _activeRequest?.Dispose();
    }
}
