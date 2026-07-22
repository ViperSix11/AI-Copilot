using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static class PushToTalkRecordingPolicy
{
    public static readonly TimeSpan MinimumUsefulDuration = TimeSpan.FromMilliseconds(200);
    public static bool ShouldSubmit(TimeSpan duration) => duration >= MinimumUsefulDuration;
}

public sealed class GlobalPushToTalkController : IDisposable
{
    public static readonly TimeSpan MaximumRecordingDuration = TimeSpan.FromSeconds(15);

    private readonly IGlobalPushToTalkInputService _input;
    private readonly Func<GlobalPushToTalkHotkey, CancellationToken, Task<bool>> _startRecording;
    private readonly Func<CancellationToken, Task> _stopRecording;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private GlobalPushToTalkHotkey _binding = GlobalPushToTalkHotkey.Default;
    private GlobalPushToTalkHotkey? _pendingBinding;
    private TaskCompletionSource? _releaseSignal;
    private int _active;
    private bool _suspended;
    private bool _disposed;

    public GlobalPushToTalkController(
        IGlobalPushToTalkInputService input,
        Func<GlobalPushToTalkHotkey, CancellationToken, Task<bool>> startRecording,
        Func<CancellationToken, Task> stopRecording,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _startRecording = startRecording ?? throw new ArgumentNullException(nameof(startRecording));
        _stopRecording = stopRecording ?? throw new ArgumentNullException(nameof(stopRecording));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _input.HotkeyPressed += OnPressed;
        _input.HotkeyReleased += OnReleased;
        _input.RegistrationStatusChanged += OnRegistrationStatusChanged;
    }

    public event EventHandler<GlobalInputRegistrationResult>? StatusChanged;
    public event EventHandler? HotkeyDetected;
    public bool IsRecording => Volatile.Read(ref _active) != 0;
    public bool DetectionOnly { get; set; }
    public GlobalPushToTalkHotkey Binding => _binding;

    public GlobalInputRegistrationResult Configure(GlobalPushToTalkHotkey binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binding.Validate();
        if (binding.Enabled) DetectionOnly = false;
        _suspended = false;
        if (IsRecording)
        {
            _pendingBinding = binding;
            return Publish(GlobalInputRegistrationResult.Deferred);
        }

        _binding = binding;
        return Publish(_input.Configure(binding));
    }

    public void Suspend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _suspended = true;
        _input.SuspendRecognition();
        Publish(new(true, "suspended", "Press the desired key combination..."));
    }

    public GlobalInputRegistrationResult Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _suspended = false;
        return IsRecording
            ? Publish(GlobalInputRegistrationResult.Deferred)
            : Publish(_input.ResumeRecognition());
    }

    private async void OnPressed(object? sender, EventArgs e)
    {
        if (_disposed || _suspended || !_binding.Enabled) return;
        HotkeyDetected?.Invoke(this, EventArgs.Empty);
        if (DetectionOnly) return;
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            Publish(new(true, "busy", "Voice operation already active."));
            return;
        }

        GlobalPushToTalkHotkey frozen = _binding;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaseSignal = release;
        bool started = false;
        bool stopAttempted = false;
        try
        {
            started = await _startRecording(frozen, _shutdown.Token).ConfigureAwait(false);
            if (!started)
            {
                Publish(new(true, "busy", "Voice operation already active."));
                return;
            }

            Task timeout = _delay(MaximumRecordingDuration, _shutdown.Token);
            await Task.WhenAny(release.Task, timeout).ConfigureAwait(false);
            _shutdown.Token.ThrowIfCancellationRequested();
            stopAttempted = true;
            await _stopRecording(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            if (started && !stopAttempted)
            {
                try { await _stopRecording(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }
        catch
        {
            Publish(new(false, "activation-failed", "Push-to-talk activation failed."));
            if (started && !stopAttempted)
            {
                try { await _stopRecording(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }
        finally
        {
            if (ReferenceEquals(_releaseSignal, release)) _releaseSignal = null;
            Interlocked.Exchange(ref _active, 0);
            if (!_disposed && _pendingBinding is { } pending)
            {
                _pendingBinding = null;
                _binding = pending;
                Publish(_input.Configure(pending));
            }
        }
    }

    private void OnReleased(object? sender, EventArgs e) => _releaseSignal?.TrySetResult();

    private void OnRegistrationStatusChanged(object? sender, GlobalInputRegistrationResult result)
        => Publish(result);

    private GlobalInputRegistrationResult Publish(GlobalInputRegistrationResult value)
    {
        StatusChanged?.Invoke(this, value);
        return value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _releaseSignal?.TrySetResult();
        _input.HotkeyPressed -= OnPressed;
        _input.HotkeyReleased -= OnReleased;
        _input.RegistrationStatusChanged -= OnRegistrationStatusChanged;
        _shutdown.Dispose();
    }
}
