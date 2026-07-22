using NAudio.Wave;

namespace ArmaAiBridge.App.Services;

public sealed class WindowsAudioPlaybackService : IAudioPlaybackService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private WaveOutEvent? _activeOutput;
    private bool _disposed;

    public async Task PlayAsync(AudioPayload audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Audio playback is already active.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using MemoryStream memory = new(audio.Bytes.ToArray(), writable: false);
            using WaveStream reader = CreateReader(audio.MediaType, memory);
            using WaveOutEvent output = new();
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is null) completion.TrySetResult();
                else completion.TrySetException(args.Exception);
            };
            lock (_sync) _activeOutput = output;
            using CancellationTokenRegistration registration = cancellationToken.Register(output.Stop);
            output.Init(reader);
            output.Play();
            await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new SpeechServiceException(
                "Audio playback failed. Check the default Windows output device.",
                "windows-audio",
                "playback",
                innerException: exception);
        }
        finally
        {
            lock (_sync) _activeOutput = null;
            _gate.Release();
        }
    }

    private static WaveStream CreateReader(string mediaType, Stream stream)
        => mediaType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) ||
           mediaType.Equals("audio/wave", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(stream)
            : mediaType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase) ||
              mediaType.Equals("audio/mp3", StringComparison.OrdinalIgnoreCase)
                ? new Mp3FileReader(stream)
                : throw new InvalidOperationException("Unsupported audio format.");

    public void Stop()
    {
        lock (_sync)
        {
            try { _activeOutput?.Stop(); }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _gate.Dispose();
    }
}
