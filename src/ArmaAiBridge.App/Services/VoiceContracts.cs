using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public enum UserTurnSource
{
    Typed,
    Spoken
}

public enum VoiceStage
{
    Ready,
    Listening,
    Recording,
    Transcribing,
    Thinking,
    GeneratingVoice,
    Speaking,
    Failed
}

public sealed class AudioPayload : IDisposable
{
    private byte[]? _bytes;

    public AudioPayload(byte[] bytes, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new ArgumentException("Audio payload cannot be empty.", nameof(bytes));
        _bytes = bytes;
        MediaType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType;
    }

    public string MediaType { get; }
    public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(AudioPayload));

    public AudioPayload Clone() => new(Bytes.ToArray(), MediaType);

    public void Dispose()
    {
        byte[]? bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) Array.Clear(bytes);
    }
}

public interface IAudioRecording : IAsyncDisposable
{
    string FilePath { get; }
    TimeSpan Duration { get; }
    Task<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

public interface IMicrophoneCaptureSession : IAsyncDisposable
{
    Task<IAudioRecording> Completion { get; }
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IMicrophoneCaptureService
{
    Task<IMicrophoneCaptureSession> StartAsync(CancellationToken cancellationToken);
}

public interface IVoiceActivatedMicrophoneCaptureService
{
    Task<IAudioRecording> CaptureUtteranceAsync(CancellationToken cancellationToken);
}

public interface ISpeechToTextService
{
    Task<string> TranscribeAsync(
        IAudioRecording recording,
        string apiKey,
        CancellationToken cancellationToken);
}

public interface ITextToSpeechService
{
    Task<AudioPayload> SynthesizeAsync(
        string text,
        string apiKey,
        string voiceId,
        CancellationToken cancellationToken);
}

public interface IAudioPlaybackService : IDisposable
{
    Task PlayAsync(AudioPayload audio, CancellationToken cancellationToken);
    void Stop();
}

public interface IOpenAiAssistantService : IDisposable
{
    Task<AssistantResponse> AskAsync(
        string apiKey,
        string model,
        string question,
        string worldSnapshotJson,
        ResponseProfileSettings responseProfile,
        Func<string, System.Text.Json.JsonElement, CancellationToken, Task<string>> executeTool,
        CancellationToken cancellationToken);

    Task<AssistantResponse> AskEventAsync(
        string apiKey,
        string model,
        string normalizedEventJson,
        string worldSnapshotJson,
        ResponseProfileSettings responseProfile,
        Func<string, System.Text.Json.JsonElement, CancellationToken, Task<string>> executeTool,
        CancellationToken cancellationToken)
        => AskAsync(
            apiKey,
            model,
            normalizedEventJson,
            worldSnapshotJson,
            responseProfile,
            executeTool,
            cancellationToken);

    void ResetConversation();
}

public interface IAssistantTurnService
{
    Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        CancellationToken cancellationToken);

    Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        Action<RadioAcknowledgement>? acknowledgementReady,
        CancellationToken cancellationToken)
        => SubmitUserTurnAsync(text, source, cancellationToken);

    async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        Func<RadioAcknowledgement, CancellationToken, Task>? acknowledgementReady,
        Action<AssistantResponse>? answerTextReady,
        CancellationToken cancellationToken)
    {
        Task acknowledgementDelivery = Task.CompletedTask;
        AssistantResponse answer = await SubmitUserTurnAsync(
            text,
            source,
            acknowledgementReady is null
                ? null
                : acknowledgement => acknowledgementDelivery = acknowledgementReady(acknowledgement, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        answerTextReady?.Invoke(answer);
        await acknowledgementDelivery.ConfigureAwait(false);
        return answer;
    }

    void ResetConversation();
}

public sealed record VoiceProviderSettings(
    string OpenAiApiKey,
    string ElevenLabsApiKey,
    string ElevenLabsVoiceId);

public sealed record VoiceTurnResult(
    string Transcript,
    AssistantResponse Answer,
    AudioPayload? Audio,
    Exception? SpeechFailure)
{
    public bool SpeechSucceeded => SpeechFailure is null;
}

public sealed record SpeechOutputResult(
    AudioPayload? Audio,
    Exception? Failure)
{
    public bool Succeeded => Failure is null;
}
