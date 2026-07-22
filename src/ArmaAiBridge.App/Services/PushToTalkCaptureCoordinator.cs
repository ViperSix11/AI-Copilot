namespace ArmaAiBridge.App.Services;

public sealed class PushToTalkCaptureCoordinator : IAsyncDisposable
{
    private readonly IMicrophoneCaptureService _microphone;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMicrophoneCaptureSession? _session;
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _disposed;

    public PushToTalkCaptureCoordinator(IMicrophoneCaptureService microphone)
        => _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));

    public bool IsRecording => Volatile.Read(ref _session) is not null;

    public async Task<IAudioRecording> BeginAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IMicrophoneCaptureSession session;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null) throw new InvalidOperationException("Microphone capture is already active.");
            session = await _microphone.StartAsync(cancellationToken).ConfigureAwait(false);
            _session = session;
            _cancellationRegistration = cancellationToken.Register(() => _ = ReleaseAsync());
        }
        finally
        {
            _gate.Release();
        }
        return await AwaitRecordingAsync(session).ConfigureAwait(false);
    }

    public Task ReleaseAsync()
        => Volatile.Read(ref _session)?.StopAsync() ?? Task.CompletedTask;

    private async Task<IAudioRecording> AwaitRecordingAsync(IMicrophoneCaptureSession session)
    {
        try
        {
            return await session.Completion.ConfigureAwait(false);
        }
        finally
        {
            _cancellationRegistration.Dispose();
            await session.DisposeAsync().ConfigureAwait(false);
            Interlocked.CompareExchange(ref _session, null, session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        IMicrophoneCaptureSession? session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.StopAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _cancellationRegistration.Dispose();
        _gate.Dispose();
    }
}
