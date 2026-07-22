using NAudio.Wave;

namespace ArmaAiBridge.App.Services;

public sealed class WindowsMicrophoneCaptureService : IMicrophoneCaptureService
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(15);
    public const int SampleRate = 16000;
    public const int BitsPerSample = 16;
    public const int Channels = 1;

    private readonly string _temporaryDirectory;
    private int _active;

    public WindowsMicrophoneCaptureService(string? temporaryDirectory = null)
    {
        _temporaryDirectory = temporaryDirectory ?? Path.Combine(Path.GetTempPath(), "ArmA AI Bridge", "voice");
        Directory.CreateDirectory(_temporaryDirectory);
        DeleteAbandonedRecordings();
    }

    public Task<IMicrophoneCaptureSession> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("Microphone capture is already active.");

        try
        {
            string path = Path.Combine(_temporaryDirectory, $"capture-{Guid.NewGuid():N}.wav");
            IMicrophoneCaptureSession session = new CaptureSession(path, () => Volatile.Write(ref _active, 0));
            return Task.FromResult(session);
        }
        catch
        {
            Volatile.Write(ref _active, 0);
            throw;
        }
    }

    private void DeleteAbandonedRecordings()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(_temporaryDirectory, "capture-*.wav"))
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CaptureSession : IMicrophoneCaptureSession
    {
        private readonly WaveInEvent _waveIn;
        private readonly WaveFileWriter _writer;
        private readonly TaskCompletionSource<IAudioRecording> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _maximumTimer = new();
        private readonly Action _released;
        private readonly string _path;
        private long _pcmBytes;
        private int _stopping;
        private int _disposed;

        public CaptureSession(string path, Action released)
        {
            _path = path;
            _released = released;
            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                    BufferMilliseconds = 100
                };
                _writer = new WaveFileWriter(path, _waveIn.WaveFormat);
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;
                _waveIn.StartRecording();
                _ = StopAtLimitAsync();
            }
            catch
            {
                try { File.Delete(path); } catch { }
                released();
                throw;
            }
        }

        public Task<IAudioRecording> Completion => _completion.Task;

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _stopping, 1) == 0)
            {
                _maximumTimer.Cancel();
                try { _waveIn.StopRecording(); }
                catch (Exception exception) { CompleteFailure(exception); }
            }
            return Task.CompletedTask;
        }

        private async Task StopAtLimitAsync()
        {
            try
            {
                await Task.Delay(MaximumDuration, _maximumTimer.Token).ConfigureAwait(false);
                await StopAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_maximumTimer.IsCancellationRequested)
            {
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (Volatile.Read(ref _stopping) != 0 || e.BytesRecorded <= 0) return;
            long maximumPcmBytes = SampleRate * (BitsPerSample / 8L) * Channels * (long)MaximumDuration.TotalSeconds;
            int allowed = (int)Math.Min(e.BytesRecorded, Math.Max(0, maximumPcmBytes - _pcmBytes));
            if (allowed > 0)
            {
                _writer.Write(e.Buffer, 0, allowed);
                _pcmBytes += allowed;
            }
            if (_pcmBytes >= maximumPcmBytes) _ = StopAsync();
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _maximumTimer.Cancel();
            try
            {
                _writer.Dispose();
                _waveIn.Dispose();
                _released();
                if (e.Exception is not null) throw e.Exception;
                if (_pcmBytes == 0) throw new InvalidOperationException("No microphone audio was captured.");
                double seconds = _pcmBytes / (double)(SampleRate * (BitsPerSample / 8) * Channels);
                _completion.TrySetResult(new TemporaryAudioRecording(_path, TimeSpan.FromSeconds(seconds)));
            }
            catch (Exception exception)
            {
                CompleteFailure(exception);
            }
        }

        private void CompleteFailure(Exception exception)
        {
            _released();
            try { _writer.Dispose(); } catch { }
            try { _waveIn.Dispose(); } catch { }
            try { File.Delete(_path); } catch { }
            _completion.TrySetException(new SpeechServiceException(
                "Microphone capture failed. Check the default Windows input device.",
                "windows-audio",
                "recording",
                innerException: exception));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await StopAsync().ConfigureAwait(false);
            _maximumTimer.Dispose();
        }
    }
}

public sealed class TemporaryAudioRecording : IAudioRecording
{
    private string? _path;

    public TemporaryAudioRecording(string path, TimeSpan duration)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        Duration = duration;
    }

    public string FilePath => _path ?? throw new ObjectDisposedException(nameof(TemporaryAudioRecording));
    public TimeSpan Duration { get; }

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public ValueTask DisposeAsync()
    {
        string? path = Interlocked.Exchange(ref _path, null);
        if (path is not null)
        {
            try { File.Delete(path); }
            catch (FileNotFoundException) { }
        }
        return ValueTask.CompletedTask;
    }
}
