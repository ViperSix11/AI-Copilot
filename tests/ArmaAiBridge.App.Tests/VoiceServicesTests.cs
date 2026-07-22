using System.Net;
using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class VoiceServicesTests
{
    [Fact]
    public void MicrophoneContract_IsBoundedToFifteenSecondsAndPcmWaveFormat()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), WindowsMicrophoneCaptureService.MaximumDuration);
        Assert.Equal(16000, WindowsMicrophoneCaptureService.SampleRate);
        Assert.Equal(16, WindowsMicrophoneCaptureService.BitsPerSample);
        Assert.Equal(1, WindowsMicrophoneCaptureService.Channels);
    }

    [Fact]
    public async Task TemporaryRecording_DisposalDeletesFileDeterministically()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"aab-voice-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "capture-test.wav");
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("RIFF-test"), TestContext.Current.CancellationToken);

        TemporaryAudioRecording recording = new(path, TimeSpan.FromSeconds(1));
        await recording.DisposeAsync();

        Assert.False(File.Exists(path));
        Directory.Delete(directory);
    }

    [Fact]
    public async Task PushToTalk_StartReleaseAndOverlapLifecycleIsDeterministic()
    {
        FakeCaptureSession session = new();
        FakeMicrophone microphone = new(session);
        await using PushToTalkCaptureCoordinator capture = new(microphone);

        Task<IAudioRecording> recordingTask = capture.BeginAsync(TestContext.Current.CancellationToken);
        await session.Started.Task;
        Assert.True(capture.IsRecording);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.BeginAsync(TestContext.Current.CancellationToken));

        await capture.ReleaseAsync();
        IAudioRecording recording = await recordingTask;

        Assert.Equal(1, microphone.StartCount);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.False(capture.IsRecording);
        await recording.DisposeAsync();
    }

    [Fact]
    public async Task PushToTalk_CancellationStopsCaptureAndCompletesCleanup()
    {
        FakeCaptureSession session = new();
        FakeMicrophone microphone = new(session);
        await using PushToTalkCaptureCoordinator capture = new(microphone);
        using CancellationTokenSource cancellation = new();

        Task<IAudioRecording> recordingTask = capture.BeginAsync(cancellation.Token);
        await session.Started.Task;
        cancellation.Cancel();
        IAudioRecording recording = await recordingTask;

        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
        await recording.DisposeAsync();
    }

    [Fact]
    public async Task AssemblyAi_UploadsCreatesPollsAndReturnsOneFinalTranscript()
    {
        int pollCount = 0;
        Recording recording = new(Encoding.ASCII.GetBytes("bounded-wave"));
        ScriptedHttpHandler handler = new(async request =>
        {
            Assert.Equal("assembly-secret", request.Headers.GetValues("Authorization").Single());
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload", StringComparison.Ordinal))
            {
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                Assert.Equal("bounded-wave", Encoding.ASCII.GetString(await request.Content.ReadAsByteArrayAsync()));
                return Json(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn.assemblyai.com/upload/private\"}");
            }
            if (request.Method == HttpMethod.Post)
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.True(body.RootElement.GetProperty("language_detection").GetBoolean());
                Assert.Equal("https://cdn.assemblyai.com/upload/private", body.RootElement.GetProperty("audio_url").GetString());
                return Json(HttpStatusCode.OK, "{\"id\":\"transcript-private\"}");
            }

            pollCount++;
            return pollCount == 1
                ? Json(HttpStatusCode.OK, "{\"status\":\"queued\"}")
                : Json(HttpStatusCode.OK, "{\"status\":\"completed\",\"text\":\"Papa Bear, welche Position habe ich?\"}");
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.assemblyai.com/v2/") };
        using AssemblyAiSpeechToTextService service = new(http, (_, _) => Task.CompletedTask);

        string transcript = await service.TranscribeAsync(recording, "assembly-secret", TestContext.Current.CancellationToken);

        Assert.Equal("Papa Bear, welche Position habe ich?", transcript);
        Assert.Equal(2, pollCount);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task AssemblyAi_InvalidCredentialIsActionableAndSecretFree()
    {
        Recording recording = new(Encoding.ASCII.GetBytes("audio-secret"));
        ScriptedHttpHandler handler = new(_ => Task.FromResult(Json(
            HttpStatusCode.Unauthorized,
            "{\"error\":\"assembly-secret audio-secret\"}")));
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.assemblyai.com/v2/") };
        using AssemblyAiSpeechToTextService service = new(http);

        SpeechServiceException exception = await Assert.ThrowsAsync<SpeechServiceException>(() =>
            service.TranscribeAsync(recording, "assembly-secret", TestContext.Current.CancellationToken));
        string log = SpeechServiceException.FormatForLog(exception);

        Assert.Contains("valid key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assembly-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("audio-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("assembly-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("audio-secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssemblyAi_CancellationDuringPollStopsBeforeAnotherRequest()
    {
        Recording recording = new(Encoding.ASCII.GetBytes("wave"));
        TaskCompletionSource delayEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedHttpHandler handler = new(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/upload", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn.assemblyai.com/upload/x\"}")
                : request.Method == HttpMethod.Post
                    ? Json(HttpStatusCode.OK, "{\"id\":\"id\"}")
                    : Json(HttpStatusCode.OK, "{\"status\":\"processing\"}")));
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.assemblyai.com/v2/") };
        using AssemblyAiSpeechToTextService service = new(http, async (_, token) =>
        {
            delayEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using CancellationTokenSource cancellation = new();

        Task<string> task = service.TranscribeAsync(recording, "key", cancellation.Token);
        await delayEntered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task AssemblyAi_PollDeadlineReturnsTypedTimeout()
    {
        Recording recording = new(Encoding.ASCII.GetBytes("wave"));
        ScriptedHttpHandler handler = new(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/upload", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn.assemblyai.com/upload/x\"}")
                : request.Method == HttpMethod.Post
                    ? Json(HttpStatusCode.OK, "{\"id\":\"id\"}")
                    : Json(HttpStatusCode.OK, "{\"status\":\"processing\"}")));
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.assemblyai.com/v2/") };
        using AssemblyAiSpeechToTextService service = new(
            http,
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
            TimeSpan.FromMilliseconds(20));

        SpeechServiceException exception = await Assert.ThrowsAsync<SpeechServiceException>(() =>
            service.TranscribeAsync(recording, "key", TestContext.Current.CancellationToken));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("poll_timeout", exception.Stage);
    }

    [Fact]
    public async Task ProviderNetworkFailuresUseSecretFreeTypedErrors()
    {
        ScriptedHttpHandler handler = new(_ => throw new HttpRequestException("provider leaked secret-key"));
        using HttpClient assemblyHttp = new(handler) { BaseAddress = new Uri("https://api.assemblyai.com/v2/") };
        using AssemblyAiSpeechToTextService assembly = new(assemblyHttp);

        SpeechServiceException assemblyError = await Assert.ThrowsAsync<SpeechServiceException>(() =>
            assembly.TranscribeAsync(new Recording(new byte[] { 1 }), "secret-key", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret-key", assemblyError.Message, StringComparison.Ordinal);
        Assert.Equal("assemblyai", assemblyError.Provider);

        using HttpClient elevenHttp = new(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/v1/") };
        using ElevenLabsTextToSpeechService eleven = new(elevenHttp);
        SpeechServiceException elevenError = await Assert.ThrowsAsync<SpeechServiceException>(() =>
            eleven.SynthesizeAsync("private-answer", "secret-key", "private-voice", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret-key", elevenError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-answer", SpeechServiceException.FormatForLog(elevenError), StringComparison.Ordinal);
        Assert.DoesNotContain("private-voice", SpeechServiceException.FormatForLog(elevenError), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElevenLabs_UsesConfiguredVoiceMultilingualModelAndBoundedMp3()
    {
        byte[] expectedAudio = { 1, 2, 3, 4 };
        ScriptedHttpHandler handler = new(async request =>
        {
            Assert.Equal("eleven-secret", request.Headers.GetValues("xi-api-key").Single());
            Assert.Equal("/v1/text-to-speech/voice%20id", request.RequestUri!.AbsolutePath);
            Assert.Equal("mp3_44100_128", ParseQuery(request.RequestUri.Query)["output_format"]);
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Final position answer", body.RootElement.GetProperty("text").GetString());
            Assert.Equal("eleven_multilingual_v2", body.RootElement.GetProperty("model_id").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedAudio)
            };
        });
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/v1/") };
        using ElevenLabsTextToSpeechService service = new(http);

        using AudioPayload result = await service.SynthesizeAsync(
            "Final position answer",
            "eleven-secret",
            "voice id",
            TestContext.Current.CancellationToken);

        Assert.Equal("audio/mpeg", result.MediaType);
        Assert.Equal(expectedAudio, result.Bytes.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "API key")]
    [InlineData(HttpStatusCode.NotFound, "voice ID")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "voice ID")]
    public async Task ElevenLabs_CredentialAndVoiceErrorsAreActionableAndSecretFree(
        HttpStatusCode status,
        string expectedMessage)
    {
        ScriptedHttpHandler handler = new(_ => Task.FromResult(Json(status, "{\"detail\":\"eleven-secret voice-secret answer-secret\"}")));
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/v1/") };
        using ElevenLabsTextToSpeechService service = new(http);

        SpeechServiceException exception = await Assert.ThrowsAsync<SpeechServiceException>(() =>
            service.SynthesizeAsync("answer-secret", "eleven-secret", "voice-secret", TestContext.Current.CancellationToken));
        string log = SpeechServiceException.FormatForLog(exception);

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eleven-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("answer-secret", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedTurnService_UsesFreshCurrentSnapshotForTypedAndSpokenTurns()
    {
        FakeOpenAi assistant = new();
        int snapshot = 0;
        using AssistantTurnService turns = new(
            assistant,
            () =>
            {
                snapshot++;
                return (true, $"{{\"schema\":\"{WorldSnapshotBuilder.SnapshotSchema}\",\"map\":{{\"name\":\"Altis\",\"grid\":\"{snapshot:000000}\"}},\"player\":{{\"positionAsl\":[{snapshot},2,3],\"freshnessClass\":\"live\",\"confidence\":1.0}}}}");
            },
            _ => Task.FromResult(("openai-secret", "gpt-5-mini")),
            (_, _, _) => Task.FromResult("tool"));

        await turns.SubmitUserTurnAsync("typed", UserTurnSource.Typed, TestContext.Current.CancellationToken);
        await turns.SubmitUserTurnAsync("Papa Bear, welche Position habe ich?", UserTurnSource.Spoken, TestContext.Current.CancellationToken);

        Assert.Equal(2, assistant.Calls.Count);
        Assert.Contains("\"grid\":\"000001\"", assistant.Calls[0].Snapshot, StringComparison.Ordinal);
        Assert.Contains("\"grid\":\"000002\"", assistant.Calls[1].Snapshot, StringComparison.Ordinal);
        Assert.Contains("\"positionAsl\":[2,2,3]", assistant.Calls[1].Snapshot, StringComparison.Ordinal);
        Assert.Contains("\"freshnessClass\":\"live\"", assistant.Calls[1].Snapshot, StringComparison.Ordinal);
        Assert.Contains("\"confidence\":1", assistant.Calls[1].Snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoiceTurn_TranscribesOnceUsesSharedTurnAndSpeaksOnlyFinalAnswer()
    {
        FakeSpeechToText speech = new("Papa Bear, welche Position habe ich?");
        FakeTurnService turns = new(new AssistantResponse("Altis, grid 123456.", "gpt-5-mini", 0, 10, 5));
        FakeTextToSpeech synthesis = new();
        FakePlayback playback = new();
        using VoiceInteractionService voice = CreateVoice(speech, turns, synthesis, playback);
        Recording recording = new(Encoding.ASCII.GetBytes("RIFF-wave"));
        List<VoiceStage> stages = new();

        VoiceTurnResult result = await voice.RunVoiceTurnAsync(
            recording,
            new InlineProgress<VoiceStage>(stages.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, speech.CallCount);
        Assert.Single(turns.Calls);
        Assert.Equal(UserTurnSource.Spoken, turns.Calls[0].Source);
        Assert.Equal("Papa Bear, welche Position habe ich?", result.Transcript);
        Assert.Equal(new[] { "Altis, grid 123456." }, synthesis.Texts);
        Assert.Equal(1, playback.CallCount);
        Assert.Equal(new[] { VoiceStage.Transcribing, VoiceStage.Thinking, VoiceStage.GeneratingVoice, VoiceStage.Speaking, VoiceStage.Ready }, stages);
        result.Audio.Dispose();
    }

    [Fact]
    public async Task VoiceTestsKeepNetworkBoundariesSeparated()
    {
        FakeSpeechToText speech = new("transcript-only");
        FakeTurnService turns = new(new AssistantResponse("unused", "model", 0, 0, 0));
        FakeTextToSpeech synthesis = new();
        FakePlayback playback = new();
        using VoiceInteractionService voice = CreateVoice(speech, turns, synthesis, playback);
        Recording recording = new(Encoding.ASCII.GetBytes("RIFF-wave"));

        await voice.PlayMicrophoneTestAsync(recording, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, speech.CallCount);
        Assert.Empty(turns.Calls);
        Assert.Empty(synthesis.Texts);
        Assert.Equal(1, playback.CallCount);

        string transcript = await voice.RunTranscriptionTestAsync(recording, null, TestContext.Current.CancellationToken);
        Assert.Equal("transcript-only", transcript);
        Assert.Equal(1, speech.CallCount);
        Assert.Empty(turns.Calls);
        Assert.Empty(synthesis.Texts);

        using AudioPayload voiceTest = await voice.RunVoiceTestAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { VoiceInteractionService.VoiceTestPhrase }, synthesis.Texts);
        Assert.Equal(2, playback.CallCount);
        Assert.Equal("Papa Bear online. Radio check complete.", VoiceInteractionService.VoiceTestPhrase);
    }

    [Theory]
    [InlineData(VoiceStage.Transcribing)]
    [InlineData(VoiceStage.Thinking)]
    [InlineData(VoiceStage.GeneratingVoice)]
    [InlineData(VoiceStage.Speaking)]
    public async Task VoiceTurn_CancellationStopsAtEveryAsyncStage(VoiceStage blockedStage)
    {
        BlockingPoint block = new();
        ISpeechToTextService speech = blockedStage == VoiceStage.Transcribing
            ? new BlockingSpeech(block)
            : new FakeSpeechToText("question");
        IAssistantTurnService turns = blockedStage == VoiceStage.Thinking
            ? new BlockingTurns(block)
            : new FakeTurnService(new AssistantResponse("answer", "model", 0, 0, 0));
        ITextToSpeechService synthesis = blockedStage == VoiceStage.GeneratingVoice
            ? new BlockingSynthesis(block)
            : new FakeTextToSpeech();
        IAudioPlaybackService playback = blockedStage == VoiceStage.Speaking
            ? new BlockingPlayback(block)
            : new FakePlayback();
        using VoiceInteractionService voice = CreateVoice(speech, turns, synthesis, playback);
        using CancellationTokenSource cancellation = new();

        Task<VoiceTurnResult> task = voice.RunVoiceTurnAsync(
            new Recording(Encoding.ASCII.GetBytes("wave")),
            null,
            cancellation.Token);
        await block.Entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task VoiceService_RejectsOverlappingOperations()
    {
        BlockingPoint block = new();
        using VoiceInteractionService voice = CreateVoice(
            new BlockingSpeech(block),
            new FakeTurnService(new AssistantResponse("answer", "model", 0, 0, 0)),
            new FakeTextToSpeech(),
            new FakePlayback());
        using CancellationTokenSource cancellation = new();
        Task<VoiceTurnResult> first = voice.RunVoiceTurnAsync(
            new Recording(Encoding.ASCII.GetBytes("wave")), null, cancellation.Token);
        await block.Entered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => voice.RunTranscriptionTestAsync(
            new Recording(Encoding.ASCII.GetBytes("wave")), null, TestContext.Current.CancellationToken));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    private static VoiceInteractionService CreateVoice(
        ISpeechToTextService speech,
        IAssistantTurnService turns,
        ITextToSpeechService synthesis,
        IAudioPlaybackService playback)
        => new(
            speech,
            turns,
            synthesis,
            playback,
            _ => Task.FromResult(new VoiceProviderSettings("assembly-key", "eleven-key", "voice-id")));

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .ToDictionary(value => Uri.UnescapeDataString(value[0]), value => Uri.UnescapeDataString(value[1]));

    private sealed class Recording : IAudioRecording
    {
        private readonly byte[] _bytes;
        public Recording(byte[] bytes) => _bytes = bytes;
        public string FilePath => "test.wav";
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMicrophone : IMicrophoneCaptureService
    {
        private readonly FakeCaptureSession _session;
        public FakeMicrophone(FakeCaptureSession session) => _session = session;
        public int StartCount { get; private set; }
        public Task<IMicrophoneCaptureSession> StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            _session.Started.TrySetResult();
            return Task.FromResult<IMicrophoneCaptureSession>(_session);
        }
    }

    private sealed class FakeCaptureSession : IMicrophoneCaptureSession
    {
        private readonly TaskCompletionSource<IAudioRecording> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task<IAudioRecording> Completion => _completion.Task;
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _completion.TrySetResult(new Recording(Encoding.ASCII.GetBytes("wave")));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        private int _requestCount;
        public ScriptedHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        public int RequestCount => Volatile.Read(ref _requestCount);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            cancellationToken.ThrowIfCancellationRequested();
            return _handler(request);
        }
    }

    private sealed class FakeOpenAi : IOpenAiAssistantService
    {
        public List<(string Question, string Snapshot)> Calls { get; } = new();
        public Task<AssistantResponse> AskAsync(
            string apiKey,
            string model,
            string question,
            string worldSnapshotJson,
            Func<string, JsonElement, CancellationToken, Task<string>> executeTool,
            CancellationToken cancellationToken)
        {
            Calls.Add((question, worldSnapshotJson));
            return Task.FromResult(new AssistantResponse("answer", model, 0, 1, 1));
        }
        public void ResetConversation() { }
        public void Dispose() { }
    }

    private sealed class FakeSpeechToText : ISpeechToTextService
    {
        private readonly string _transcript;
        public FakeSpeechToText(string transcript) => _transcript = transcript;
        public int CallCount { get; private set; }
        public Task<string> TranscribeAsync(IAudioRecording recording, string apiKey, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_transcript);
        }
    }

    private sealed class FakeTurnService : IAssistantTurnService
    {
        private readonly AssistantResponse _response;
        public FakeTurnService(AssistantResponse response) => _response = response;
        public List<(string Text, UserTurnSource Source)> Calls { get; } = new();
        public Task<AssistantResponse> SubmitUserTurnAsync(string text, UserTurnSource source, CancellationToken cancellationToken)
        {
            Calls.Add((text, source));
            return Task.FromResult(_response);
        }
        public void ResetConversation() { }
    }

    private sealed class FakeTextToSpeech : ITextToSpeechService
    {
        public List<string> Texts { get; } = new();
        public Task<AudioPayload> SynthesizeAsync(string text, string apiKey, string voiceId, CancellationToken cancellationToken)
        {
            Texts.Add(text);
            return Task.FromResult(new AudioPayload(new byte[] { 1, 2, 3 }, "audio/mpeg"));
        }
    }

    private class FakePlayback : IAudioPlaybackService
    {
        public int CallCount { get; private set; }
        public virtual Task PlayAsync(AudioPayload audio, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
        public virtual void Stop() { }
        public virtual void Dispose() { }
    }

    private sealed class BlockingPoint
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class BlockingSpeech : ISpeechToTextService
    {
        private readonly BlockingPoint _block;
        public BlockingSpeech(BlockingPoint block) => _block = block;
        public async Task<string> TranscribeAsync(IAudioRecording recording, string apiKey, CancellationToken cancellationToken)
        {
            await _block.WaitAsync(cancellationToken);
            return "question";
        }
    }

    private sealed class BlockingTurns : IAssistantTurnService
    {
        private readonly BlockingPoint _block;
        public BlockingTurns(BlockingPoint block) => _block = block;
        public async Task<AssistantResponse> SubmitUserTurnAsync(string text, UserTurnSource source, CancellationToken cancellationToken)
        {
            await _block.WaitAsync(cancellationToken);
            return new AssistantResponse("answer", "model", 0, 0, 0);
        }
        public void ResetConversation() { }
    }

    private sealed class BlockingSynthesis : ITextToSpeechService
    {
        private readonly BlockingPoint _block;
        public BlockingSynthesis(BlockingPoint block) => _block = block;
        public async Task<AudioPayload> SynthesizeAsync(string text, string apiKey, string voiceId, CancellationToken cancellationToken)
        {
            await _block.WaitAsync(cancellationToken);
            return new AudioPayload(new byte[] { 1 }, "audio/mpeg");
        }
    }

    private sealed class BlockingPlayback : FakePlayback
    {
        private readonly BlockingPoint _block;
        public BlockingPlayback(BlockingPoint block) => _block = block;
        public override Task PlayAsync(AudioPayload audio, CancellationToken cancellationToken)
            => _block.WaitAsync(cancellationToken);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}
