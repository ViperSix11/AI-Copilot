namespace ArmaAiBridge.App.Services;

public sealed class VoiceInteractionService : IDisposable
{
    public const string VoiceTestPhrase = "Papa Bear online. Radio check complete.";
    private const int MaximumRecordingBytes = 600 * 1024;

    private readonly ISpeechToTextService _speechToText;
    private readonly IAssistantTurnService _assistantTurns;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly IAudioPlaybackService _playback;
    private readonly Func<CancellationToken, Task<VoiceProviderSettings>> _settingsFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public VoiceInteractionService(
        ISpeechToTextService speechToText,
        IAssistantTurnService assistantTurns,
        ITextToSpeechService textToSpeech,
        IAudioPlaybackService playback,
        Func<CancellationToken, Task<VoiceProviderSettings>> settingsFactory)
    {
        _speechToText = speechToText ?? throw new ArgumentNullException(nameof(speechToText));
        _assistantTurns = assistantTurns ?? throw new ArgumentNullException(nameof(assistantTurns));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
    }

    public async Task PlayMicrophoneTestAsync(
        IAudioRecording recording,
        IProgress<VoiceStage>? progress,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(VoiceStage.Speaking);
            using AudioPayload audio = await ReadRecordingAsync(recording, cancellationToken).ConfigureAwait(false);
            await _playback.PlayAsync(audio, cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.Ready);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<string> RunTranscriptionTestAsync(
        IAudioRecording recording,
        IProgress<VoiceStage>? progress,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VoiceProviderSettings settings = await _settingsFactory(cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.Transcribing);
            string transcript = await _speechToText.TranscribeAsync(
                recording,
                settings.AssemblyAiApiKey,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.Ready);
            return transcript;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<VoiceTurnResult> RunVoiceTurnAsync(
        IAudioRecording recording,
        IProgress<VoiceStage>? progress,
        Action<string> transcriptReady,
        Action<Models.AssistantResponse> answerReady,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcriptReady);
        ArgumentNullException.ThrowIfNull(answerReady);
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        AudioPayload? audio = null;
        try
        {
            VoiceProviderSettings settings = await _settingsFactory(cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.Transcribing);
            string transcript = await _speechToText.TranscribeAsync(
                recording,
                settings.AssemblyAiApiKey,
                cancellationToken).ConfigureAwait(false);
            transcriptReady(transcript);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(VoiceStage.Thinking);
            Models.AssistantResponse answer = await _assistantTurns.SubmitUserTurnAsync(
                transcript,
                UserTurnSource.Spoken,
                cancellationToken).ConfigureAwait(false);
            answerReady(answer);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(VoiceStage.GeneratingVoice);
            try
            {
                audio = await _textToSpeech.SynthesizeAsync(
                    answer.Text,
                    settings.ElevenLabsApiKey,
                    settings.ElevenLabsVoiceId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report(VoiceStage.Failed);
                return new VoiceTurnResult(transcript, answer, null, exception);
            }
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(VoiceStage.Speaking);
            try
            {
                await _playback.PlayAsync(audio, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report(VoiceStage.Failed);
                VoiceTurnResult partial = new(transcript, answer, audio, exception);
                audio = null;
                return partial;
            }
            progress?.Report(VoiceStage.Ready);
            VoiceTurnResult result = new(transcript, answer, audio, null);
            audio = null;
            return result;
        }
        finally
        {
            audio?.Dispose();
            _operationGate.Release();
        }
    }

    public async Task<AudioPayload> RunVoiceTestAsync(
        IProgress<VoiceStage>? progress,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        AudioPayload? audio = null;
        try
        {
            VoiceProviderSettings settings = await _settingsFactory(cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.GeneratingVoice);
            audio = await _textToSpeech.SynthesizeAsync(
                VoiceTestPhrase,
                settings.ElevenLabsApiKey,
                settings.ElevenLabsVoiceId,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.Speaking);
            await _playback.PlayAsync(audio, cancellationToken).ConfigureAwait(false);
            progress?.Report(VoiceStage.Ready);
            AudioPayload result = audio;
            audio = null;
            return result;
        }
        finally
        {
            audio?.Dispose();
            _operationGate.Release();
        }
    }

    public async Task<SpeechOutputResult> RetrySpeechAsync(
        string text,
        AudioPayload? reusableAudio,
        IProgress<VoiceStage>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("There is no text answer to speak.");
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        AudioPayload? createdAudio = null;
        try
        {
            AudioPayload? audio = reusableAudio;
            if (audio is null)
            {
                VoiceProviderSettings settings = await _settingsFactory(cancellationToken).ConfigureAwait(false);
                progress?.Report(VoiceStage.GeneratingVoice);
                try
                {
                    createdAudio = await _textToSpeech.SynthesizeAsync(
                        text,
                        settings.ElevenLabsApiKey,
                        settings.ElevenLabsVoiceId,
                        cancellationToken).ConfigureAwait(false);
                    audio = createdAudio;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    progress?.Report(VoiceStage.Failed);
                    return new SpeechOutputResult(null, exception);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(VoiceStage.Speaking);
            try
            {
                await _playback.PlayAsync(audio, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                progress?.Report(VoiceStage.Failed);
                SpeechOutputResult partial = new(audio, exception);
                createdAudio = null;
                return partial;
            }
            progress?.Report(VoiceStage.Ready);
            SpeechOutputResult result = new(audio, null);
            createdAudio = null;
            return result;
        }
        finally
        {
            createdAudio?.Dispose();
            _operationGate.Release();
        }
    }

    public void StopPlayback() => _playback.Stop();

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Another voice operation is already active.");
    }

    private static async Task<AudioPayload> ReadRecordingAsync(
        IAudioRecording recording,
        CancellationToken cancellationToken)
    {
        await using Stream input = await recording.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumRecordingBytes)
                throw new SpeechServiceException(
                    "The microphone recording exceeded the 15-second limit.",
                    "windows-audio",
                    "recording_limit");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0)
            throw new SpeechServiceException(
                "No microphone audio was captured.",
                "windows-audio",
                "recording_empty");
        return new AudioPayload(output.ToArray(), "audio/wav");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _playback.Stop();
        _playback.Dispose();
        if (_speechToText is IDisposable disposableStt) disposableStt.Dispose();
        if (_textToSpeech is IDisposable disposableTts) disposableTts.Dispose();
        _operationGate.Dispose();
    }
}
