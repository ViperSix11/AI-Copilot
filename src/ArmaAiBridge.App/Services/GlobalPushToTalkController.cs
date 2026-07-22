using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static class PushToTalkRecordingPolicy
{
    public static readonly TimeSpan MinimumUsefulDuration = TimeSpan.FromMilliseconds(200);
    public static bool ShouldSubmit(TimeSpan duration) => duration >= MinimumUsefulDuration;
}

public sealed record GlobalHotkeyRegistrationResult(bool Registered, string Code, string Message)
{
    public static GlobalHotkeyRegistrationResult Disabled { get; } = new(false, "disabled", "Disabled");
    public static GlobalHotkeyRegistrationResult Deferred { get; } = new(false, "deferred", "Registration deferred until recording ends.");
}

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Activated;
    GlobalHotkeyRegistrationResult Register(GlobalPushToTalkHotkey binding);
    void Unregister();
}

public interface IKeyStateService
{
    bool IsKeyDown(int virtualKey);
}

public sealed class GlobalPushToTalkController : IDisposable
{
    public static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(15);
    private readonly IGlobalHotkeyService _hotkey;
    private readonly IKeyStateService _keyState;
    private readonly Func<GlobalPushToTalkHotkey, CancellationToken, Task<bool>> _startRecording;
    private readonly Func<CancellationToken, Task> _stopRecording;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private GlobalPushToTalkHotkey _binding = GlobalPushToTalkHotkey.Default;
    private GlobalPushToTalkHotkey? _pendingBinding;
    private int _active;
    private bool _suspended;
    private bool _disposed;

    public GlobalPushToTalkController(
        IGlobalHotkeyService hotkey,
        IKeyStateService keyState,
        Func<GlobalPushToTalkHotkey, CancellationToken, Task<bool>> startRecording,
        Func<CancellationToken, Task> stopRecording,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _keyState = keyState ?? throw new ArgumentNullException(nameof(keyState));
        _startRecording = startRecording ?? throw new ArgumentNullException(nameof(startRecording));
        _stopRecording = stopRecording ?? throw new ArgumentNullException(nameof(stopRecording));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _hotkey.Activated += OnActivated;
    }

    public event EventHandler<GlobalHotkeyRegistrationResult>? StatusChanged;
    public bool IsRecording => Volatile.Read(ref _active) != 0;
    public GlobalPushToTalkHotkey Binding => _binding;

    public GlobalHotkeyRegistrationResult Configure(GlobalPushToTalkHotkey binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        binding.Validate();
        _suspended = false;
        _binding = binding;
        if (IsRecording)
        {
            _pendingBinding = binding;
            if (!binding.Enabled) _hotkey.Unregister();
            return Publish(GlobalHotkeyRegistrationResult.Deferred);
        }
        return RegisterCurrent();
    }

    public void Suspend()
    {
        _suspended = true;
        _hotkey.Unregister();
        Publish(new GlobalHotkeyRegistrationResult(false, "suspended", "Press the desired key combination…"));
    }

    public GlobalHotkeyRegistrationResult Resume()
    {
        _suspended = false;
        return IsRecording ? Publish(GlobalHotkeyRegistrationResult.Deferred) : RegisterCurrent();
    }

    private GlobalHotkeyRegistrationResult RegisterCurrent()
    {
        _hotkey.Unregister();
        if (_suspended) return Publish(new GlobalHotkeyRegistrationResult(false, "suspended", "Capture mode"));
        if (!_binding.Enabled) return Publish(GlobalHotkeyRegistrationResult.Disabled);
        return Publish(_hotkey.Register(_binding));
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        if (_disposed || _suspended || !_binding.Enabled) return;
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            Publish(new GlobalHotkeyRegistrationResult(true, "busy", "Voice operation already active."));
            return;
        }
        GlobalPushToTalkHotkey frozen = _binding;
        bool started = false;
        try
        {
            started = await _startRecording(frozen, _shutdown.Token).ConfigureAwait(false);
            if (!started)
            {
                Publish(new GlobalHotkeyRegistrationResult(true, "busy", "Voice operation already active."));
                return;
            }
            do { await _delay(ReleasePollInterval, _shutdown.Token).ConfigureAwait(false); }
            while (_keyState.IsKeyDown(frozen.VirtualKey));
            await _stopRecording(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            if (started) { try { await _stopRecording(CancellationToken.None).ConfigureAwait(false); } catch { } }
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
            if (!_disposed && _pendingBinding is not null)
            {
                _binding = _pendingBinding;
                _pendingBinding = null;
                RegisterCurrent();
            }
        }
    }

    private GlobalHotkeyRegistrationResult Publish(GlobalHotkeyRegistrationResult value)
    {
        StatusChanged?.Invoke(this, value);
        return value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _hotkey.Activated -= OnActivated;
        _hotkey.Unregister();
        _hotkey.Dispose();
        _shutdown.Dispose();
    }
}
