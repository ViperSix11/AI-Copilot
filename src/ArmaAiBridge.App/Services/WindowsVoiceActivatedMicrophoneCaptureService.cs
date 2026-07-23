using System.Buffers.Binary;
using NAudio.Wave;

namespace ArmaAiBridge.App.Services;

public static class VoiceActivatedCapturePolicy
{
    public const double ActivationRmsThreshold = 0.025;
    public const int ActivationBufferCount = 2;
    public static readonly TimeSpan PreRollDuration = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan EndSilenceDuration = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan MinimumVoicedDuration = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan MaximumUtteranceDuration = TimeSpan.FromSeconds(15);
}

public sealed class PcmVoiceActivitySegmenter
{
    private sealed record BufferedFrame(byte[] Data, bool Voiced);

    private readonly int _bytesPerSecond;
    private readonly int _preRollLimit;
    private readonly int _endSilenceLimit;
    private readonly int _minimumVoicedLimit;
    private readonly int _maximumUtteranceLimit;
    private readonly Queue<BufferedFrame> _preRoll = new();
    private MemoryStream? _utterance;
    private int _preRollBytes;
    private int _consecutiveVoicedBuffers;
    private int _voicedBytes;
    private int _silenceBytes;

    public PcmVoiceActivitySegmenter(
        int sampleRate = WindowsMicrophoneCaptureService.SampleRate,
        int bitsPerSample = WindowsMicrophoneCaptureService.BitsPerSample,
        int channels = WindowsMicrophoneCaptureService.Channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (bitsPerSample != 16) throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16-bit PCM is supported.");
        if (channels != 1) throw new ArgumentOutOfRangeException(nameof(channels), "Only mono PCM is supported.");

        _bytesPerSecond = sampleRate * (bitsPerSample / 8) * channels;
        _preRollLimit = BytesFor(VoiceActivatedCapturePolicy.PreRollDuration);
        _endSilenceLimit = BytesFor(VoiceActivatedCapturePolicy.EndSilenceDuration);
        _minimumVoicedLimit = BytesFor(VoiceActivatedCapturePolicy.MinimumVoicedDuration);
        _maximumUtteranceLimit = BytesFor(VoiceActivatedCapturePolicy.MaximumUtteranceDuration);
    }

    public bool IsCapturingUtterance => _utterance is not null;

    public byte[]? Process(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0) return null;
        if ((pcm.Length & 1) != 0) throw new ArgumentException("PCM input must contain complete 16-bit samples.", nameof(pcm));

        bool voiced = CalculateRms(pcm) >= VoiceActivatedCapturePolicy.ActivationRmsThreshold;
        if (_utterance is null)
        {
            byte[] frame = pcm.ToArray();
            _preRoll.Enqueue(new BufferedFrame(frame, voiced));
            _preRollBytes += frame.Length;
            TrimPreRoll();
            _consecutiveVoicedBuffers = voiced ? _consecutiveVoicedBuffers + 1 : 0;
            if (_consecutiveVoicedBuffers < VoiceActivatedCapturePolicy.ActivationBufferCount) return null;

            _utterance = new MemoryStream(Math.Min(_maximumUtteranceLimit, _preRollBytes + _bytesPerSecond));
            foreach (BufferedFrame buffered in _preRoll)
            {
                _utterance.Write(buffered.Data);
                if (buffered.Voiced) _voicedBytes += buffered.Data.Length;
            }
            _preRoll.Clear();
            _preRollBytes = 0;
            _silenceBytes = 0;
            return CompleteAtLimitIfNeeded();
        }

        int allowed = Math.Min(pcm.Length, _maximumUtteranceLimit - checked((int)_utterance.Length));
        if (allowed > 0)
        {
            _utterance.Write(pcm[..allowed]);
            if (voiced)
            {
                _voicedBytes += allowed;
                _silenceBytes = 0;
            }
            else
            {
                _silenceBytes += allowed;
            }
        }

        if (_utterance.Length >= _maximumUtteranceLimit)
            return Complete();
        if (_silenceBytes >= _endSilenceLimit)
            return Complete();
        return null;
    }

    private byte[]? CompleteAtLimitIfNeeded()
        => _utterance?.Length >= _maximumUtteranceLimit ? Complete() : null;

    private byte[]? Complete()
    {
        byte[]? result = _voicedBytes >= _minimumVoicedLimit
            ? _utterance!.ToArray()
            : null;
        Reset();
        return result;
    }

    private void Reset()
    {
        _utterance?.Dispose();
        _utterance = null;
        _preRoll.Clear();
        _preRollBytes = 0;
        _consecutiveVoicedBuffers = 0;
        _voicedBytes = 0;
        _silenceBytes = 0;
    }

    private void TrimPreRoll()
    {
        while (_preRollBytes > _preRollLimit && _preRoll.Count > 0)
            _preRollBytes -= _preRoll.Dequeue().Data.Length;
    }

    private int BytesFor(TimeSpan duration)
        => checked((int)Math.Ceiling(_bytesPerSecond * duration.TotalSeconds));

    private static double CalculateRms(ReadOnlySpan<byte> pcm)
    {
        double sumSquares = 0;
        int samples = pcm.Length / 2;
        for (int offset = 0; offset < pcm.Length; offset += 2)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..]);
            double normalized = sample / 32768d;
            sumSquares += normalized * normalized;
        }
        return Math.Sqrt(sumSquares / samples);
    }
}

public sealed class WindowsVoiceActivatedMicrophoneCaptureService : IVoiceActivatedMicrophoneCaptureService
{
    private readonly string _temporaryDirectory;
    private int _active;

    public WindowsVoiceActivatedMicrophoneCaptureService(string? temporaryDirectory = null)
    {
        _temporaryDirectory = temporaryDirectory ?? Path.Combine(Path.GetTempPath(), "ArmA AI Bridge", "voice");
        Directory.CreateDirectory(_temporaryDirectory);
        DeleteAbandonedRecordings();
    }

    public Task<IAudioRecording> CaptureUtteranceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("Voice-activated microphone capture is already active.");

        try
        {
            CaptureSession session = new(
                _temporaryDirectory,
                cancellationToken,
                () => Volatile.Write(ref _active, 0));
            return session.Completion;
        }
        catch (SpeechServiceException)
        {
            Volatile.Write(ref _active, 0);
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _active, 0);
            throw Failure(exception);
        }
    }

    private void DeleteAbandonedRecordings()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(_temporaryDirectory, "always-on-*.wav"))
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static SpeechServiceException Failure(Exception exception)
        => new(
            "Always-on microphone capture failed. Check the default Windows input device.",
            "windows-audio",
            "always-on-recording",
            innerException: exception);

    private sealed class CaptureSession
    {
        private readonly WaveInEvent _waveIn;
        private readonly PcmVoiceActivitySegmenter _segmenter = new();
        private readonly TaskCompletionSource<IAudioRecording> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _cancellationToken;
        private readonly Action _released;
        private CancellationTokenRegistration _cancellationRegistration;
        private byte[]? _utterancePcm;
        private Exception? _failure;
        private int _cancelled;
        private int _stopping;
        private int _completed;

        public CaptureSession(
            string temporaryDirectory,
            CancellationToken cancellationToken,
            Action released)
        {
            TemporaryDirectory = temporaryDirectory;
            _cancellationToken = cancellationToken;
            _released = released;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(
                    WindowsMicrophoneCaptureService.SampleRate,
                    WindowsMicrophoneCaptureService.BitsPerSample,
                    WindowsMicrophoneCaptureService.Channels),
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            try
            {
                _waveIn.StartRecording();
                _cancellationRegistration = cancellationToken.Register(Cancel);
            }
            catch (Exception exception)
            {
                CleanupDevice();
                released();
                throw Failure(exception);
            }
        }

        private string TemporaryDirectory { get; }
        public Task<IAudioRecording> Completion => _completion.Task;

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (Volatile.Read(ref _stopping) != 0 || e.BytesRecorded <= 0) return;
            try
            {
                byte[]? utterance = _segmenter.Process(e.Buffer.AsSpan(0, e.BytesRecorded));
                if (utterance is null) return;
                _utterancePcm = utterance;
                RequestStop();
            }
            catch (Exception exception)
            {
                _failure = exception;
                RequestStop();
            }
        }

        private void Cancel()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            RequestStop();
        }

        private void RequestStop()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            try
            {
                _waveIn.StopRecording();
            }
            catch (Exception exception)
            {
                Complete(exception);
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
            => Complete(e.Exception);

        private void Complete(Exception? stopFailure)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _cancellationRegistration.Unregister();
            CleanupDevice();
            _released();

            if (Volatile.Read(ref _cancelled) != 0)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            Exception? failure = _failure ?? stopFailure;
            if (failure is not null)
            {
                _completion.TrySetException(Failure(failure));
                return;
            }

            byte[]? pcm = _utterancePcm;
            if (pcm is null || pcm.Length == 0)
            {
                _completion.TrySetException(Failure(new InvalidOperationException("No utterance was captured.")));
                return;
            }

            string path = Path.Combine(TemporaryDirectory, $"always-on-{Guid.NewGuid():N}.wav");
            try
            {
                using (WaveFileWriter writer = new(path, new WaveFormat(
                    WindowsMicrophoneCaptureService.SampleRate,
                    WindowsMicrophoneCaptureService.BitsPerSample,
                    WindowsMicrophoneCaptureService.Channels)))
                {
                    writer.Write(pcm, 0, pcm.Length);
                }
                double seconds = pcm.Length / (double)(
                    WindowsMicrophoneCaptureService.SampleRate *
                    (WindowsMicrophoneCaptureService.BitsPerSample / 8) *
                    WindowsMicrophoneCaptureService.Channels);
                _completion.TrySetResult(new TemporaryAudioRecording(path, TimeSpan.FromSeconds(seconds)));
            }
            catch (Exception exception)
            {
                try { File.Delete(path); } catch { }
                _completion.TrySetException(Failure(exception));
            }
            finally
            {
                Array.Clear(pcm);
                _utterancePcm = null;
            }
        }

        private void CleanupDevice()
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try { _waveIn.Dispose(); } catch { }
        }
    }
}
