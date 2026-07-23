using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class NaturalRadioDialogueTests
{
    [Fact]
    public void ShortCalmAnswer_RemainsOneEfficientTransmission()
    {
        NaturalRadioDialogueService service = new(_ => 0);

        NaturalRadioPlan plan = service.Plan(
            "Is the route clear?",
            "Alpha 1-1, the route is clear.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.Single(plan.Transmissions);
        Assert.False(plan.AwaitingCopyConfirmation);
        Assert.Equal("Alpha 1-1, the route is clear.", plan.Transmissions[0].Text);
    }

    [Fact]
    public void ComplexOperationalAnswer_CanSplitPreparePauseAndRequestCopy()
    {
        NaturalRadioDialogueService service = new(_ => 0);
        string answer =
            "Alpha 1-1, enemy infantry has been reported northeast of Bullseye Alpha. " +
            "A second hostile vehicle is last known six hundred metres east of the industrial compound.";

        NaturalRadioPlan plan = service.Plan(
            "Any enemy activity?",
            answer,
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.Equal(3, plan.Transmissions.Count);
        Assert.Contains("Alpha 1-1", plan.Transmissions[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha 1-1", plan.Transmissions[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("Do you copy?", plan.Transmissions[^1].Text, StringComparison.Ordinal);
        Assert.All(plan.Transmissions.Skip(1), item => Assert.InRange(item.PauseBeforeMilliseconds, 250, 400));
        Assert.True(plan.AwaitingCopyConfirmation);
        Assert.Contains("split", plan.VariationId, StringComparison.Ordinal);
    }

    [Fact]
    public void SameOperationalAnswer_CanRemainSingleWithoutCopyPrompt()
    {
        NaturalRadioDialogueService service = new(_ => 99);
        string answer =
            "Alpha 1-1, enemy infantry has been reported northeast of Bullseye Alpha. " +
            "A second hostile vehicle is last known six hundred metres east of the industrial compound.";

        NaturalRadioPlan plan = service.Plan(
            "Any enemy activity?",
            answer,
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.Single(plan.Transmissions);
        Assert.False(plan.AwaitingCopyConfirmation);
        Assert.DoesNotContain("Do you copy", plan.Transmissions[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha 1-1", plan.Transmissions[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingDelayedAcknowledgement_PreventsAnotherPreparationCall()
    {
        NaturalRadioDialogueService service = new(_ => 0);

        NaturalRadioPlan plan = service.Plan(
            "Any enemy activity?",
            "Alpha 1-1, enemy infantry has been reported northeast of the industrial compound. " +
            "A second hostile vehicle is last known six hundred metres east of the same compound.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: true);

        Assert.Equal(2, plan.Transmissions.Count);
        Assert.DoesNotContain("stand by", plan.Transmissions[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalmSplit_UsesTheLongerBoundedPause()
    {
        NaturalRadioDialogueService service = new(_ => 0);

        NaturalRadioPlan plan = service.Plan(
            "Give me the logistics summary.",
            "Alpha 1-1, the first supply convoy is ready at the western depot with fuel, medical stores, and repair equipment. " +
            "The second convoy remains at the eastern depot while its final cargo inventory is checked.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.True(plan.Transmissions.Count > 1);
        Assert.All(plan.Transmissions.Skip(1), item => Assert.InRange(item.PauseBeforeMilliseconds, 500, 850));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("yes")]
    [InlineData("roger")]
    [InlineData("affirmative")]
    [InlineData("received")]
    public void CopyResponses_CloseTheOpenConfirmationState(string response)
    {
        NaturalRadioDialogueService service = new(_ => 0);
        NaturalRadioPlan original = service.Plan(
            "Hostile situation?",
            "Alpha 1-1, enemy infantry is at grid zero-six-two zero-five-five. A vehicle is nearby.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);
        Assert.True(original.AwaitingCopyConfirmation);

        Assert.True(service.TryHandleFollowUp(response, "Alpha 1-1", out NaturalRadioPlan followUp));
        Assert.False(followUp.AwaitingCopyConfirmation);
        Assert.Equal("Roger.", Assert.Single(followUp.Transmissions).Text);
        Assert.False(service.TryHandleFollowUp("copy", "Alpha 1-1", out _));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("no")]
    [InlineData("did not copy")]
    public void NegativeResponses_CloseTheOpenConfirmationState(string response)
    {
        NaturalRadioDialogueService service = new(_ => 0);
        service.Plan(
            "Hostile situation?",
            "Alpha 1-1, enemy infantry is at the industrial compound. A vehicle is nearby.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.True(service.TryHandleFollowUp(response, "Alpha 1-1", out NaturalRadioPlan followUp));
        Assert.False(followUp.AwaitingCopyConfirmation);
        Assert.Contains("clarified", Assert.Single(followUp.Transmissions).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatRequest_UsesSimplerWordingAndKeepsConfirmationOpen()
    {
        NaturalRadioDialogueService service = new(_ => 0);
        string original =
            "Alpha 1-1, be advised, enemy infantry is six hundred metres northeast of Bullseye Alpha. A hostile vehicle is nearby.";
        service.Plan("Hostile situation?", original, "Alpha 1-1", acknowledgementAlreadyEmitted: false);

        Assert.True(service.TryHandleFollowUp("say again", "Alpha 1-1", out NaturalRadioPlan repeat));

        string repeated = Assert.Single(repeat.Transmissions).Text;
        Assert.StartsWith("Alpha 1-1, say again:", repeated, StringComparison.Ordinal);
        Assert.DoesNotContain("be advised", repeated, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(original, repeated);
        Assert.True(repeat.AwaitingCopyConfirmation);
        Assert.EndsWith("Do you copy?", repeated, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenConfirmation_RemainsOpenForAnUnrecognizedReply()
    {
        NaturalRadioDialogueService service = new(_ => 0);
        service.Plan(
            "Hostile situation?",
            "Alpha 1-1, enemy infantry is at the industrial compound. A hostile vehicle is nearby.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.True(service.TryHandleFollowUp(
            "Tell me something else",
            "Alpha 1-1",
            out NaturalRadioPlan followUp));

        Assert.True(followUp.AwaitingCopyConfirmation);
        Assert.Contains("Confirm receipt", Assert.Single(followUp.Transmissions).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyRequest_WithOverTerminator_RemainsAValidOpenCall()
    {
        NaturalRadioDialogueService service = new(_ => 0);

        NaturalRadioPlan plan = service.Plan(
            "Hostile situation?",
            "Alpha 1-1, enemy infantry is at the industrial compound. A hostile vehicle is nearby. Over.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false,
            new ResponseProfileSettings { Terminator = "over" });

        string final = plan.Transmissions[^1].Text;
        Assert.EndsWith("Do you copy? Over.", final, StringComparison.Ordinal);
        Assert.DoesNotContain("Over. Do you copy", final, StringComparison.Ordinal);
    }

    [Fact]
    public void CallsignChange_DiscardsThePreviousOpenExchange()
    {
        NaturalRadioDialogueService service = new(_ => 0);
        service.Plan(
            "Hostile situation?",
            "Alpha 1-1, enemy infantry is at the compound. A hostile vehicle is nearby.",
            "Alpha 1-1",
            acknowledgementAlreadyEmitted: false);

        Assert.False(service.TryHandleFollowUp("repeat", "Bravo 2-1", out _));
    }

    [Fact]
    public async Task RepeatFollowUp_DoesNotCallOpenAiAgain()
    {
        FakeAssistant assistant = new(
            "Enemy infantry has been reported northeast of Bullseye Alpha. A hostile vehicle is nearby.");
        NaturalRadioDialogueService dialogue = new(_ => 0);
        using AssistantTurnService turns = new(
            assistant,
            () => (true, "{}"),
            _ => Task.FromResult(("key", "model", ResponseProfilePolicy.Defaults())),
            (_, _, _) => Task.FromResult("unused"),
            () => "Alpha 1-1",
            radioDialogue: dialogue);

        AssistantResponse first = await turns.SubmitUserTurnAsync(
            "Any hostile activity?",
            UserTurnSource.Typed,
            TestContext.Current.CancellationToken);
        Assert.True(first.AwaitingCopyConfirmation);

        AssistantResponse repeat = await turns.SubmitUserTurnAsync(
            "repeat",
            UserTurnSource.Spoken,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, assistant.Calls);
        Assert.Equal("local", repeat.Model);
        Assert.Contains("say again", repeat.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpokenNumberNormalization_EliminatesDigitsIncludingGroupedValues()
    {
        string spoken = RadioSpeechTextNormalizer.Normalize(
            "Alpha 1-1, range 1,000 metres, bearing 25 degrees, speed 3.5 m/s.",
            "Alpha 1-1");

        Assert.DoesNotMatch(new Regex(@"\d", RegexOptions.CultureInvariant), spoken);
        Assert.Contains("one thousand metres", spoken, StringComparison.Ordinal);
        Assert.Contains("twenty-five degrees", spoken, StringComparison.Ordinal);
        Assert.Contains("three point five metres per second", spoken, StringComparison.Ordinal);
        Assert.Contains("Alpha One-One", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoicePipeline_SynthesizesConsecutiveCallsWithOneBoundedPause()
    {
        AssistantResponse answer = new(
            "Alpha 1-1, contact update.",
            "model",
            0,
            0,
            0,
            GroupCallsign: "Alpha 1-1",
            Transmissions: new[]
            {
                new RadioTransmission("Alpha 1-1, stand by for new information."),
                new RadioTransmission("Enemy infantry is 1,000 metres northeast.", 650)
            });
        FakeTurns turns = new(answer);
        FakeTextToSpeech synthesis = new();
        FakePlayback playback = new();
        List<TimeSpan> pauses = new();
        using VoiceInteractionService voice = new(
            new FakeSpeechToText("Any contacts?"),
            turns,
            synthesis,
            playback,
            _ => Task.FromResult(new VoiceProviderSettings("openai", "eleven", "voice")),
            (duration, _) =>
            {
                pauses.Add(duration);
                return Task.CompletedTask;
            });

        VoiceTurnResult result = await voice.RunVoiceTurnAsync(
            new Recording(),
            null,
            _ => { },
            _ => { },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, synthesis.Texts.Count);
        Assert.Equal(2, playback.Calls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(650) }, pauses);
        Assert.All(synthesis.Texts, text => Assert.DoesNotMatch(@"\d", text));
        Assert.Null(result.Audio);
        Assert.True(result.SpeechSucceeded);
    }

    [Fact]
    public async Task LaterTransmissionFailure_PreservesTheCompletedVisibleAnswer()
    {
        AssistantResponse answer = new(
            "Alpha 1-1, enemy infantry is northeast of Bullseye Alpha.",
            "model",
            0,
            0,
            0,
            GroupCallsign: "Alpha 1-1",
            Transmissions: new[]
            {
                new RadioTransmission("Alpha 1-1, stand by for new information."),
                new RadioTransmission("Enemy infantry is northeast of Bullseye Alpha.", 250)
            });
        FakeTextToSpeech synthesis = new(failOnCall: 2);
        FakePlayback playback = new();
        List<AssistantResponse> visible = new();
        using VoiceInteractionService voice = new(
            new FakeSpeechToText("Any contacts?"),
            new FakeTurns(answer),
            synthesis,
            playback,
            _ => Task.FromResult(new VoiceProviderSettings("openai", "eleven", "voice")),
            (_, _) => Task.CompletedTask);

        VoiceTurnResult result = await voice.RunVoiceTurnAsync(
            new Recording(),
            null,
            _ => { },
            visible.Add,
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { answer }, visible);
        Assert.Same(answer, result.Answer);
        Assert.NotNull(result.SpeechFailure);
        Assert.Equal(2, synthesis.Texts.Count);
        Assert.Equal(1, playback.Calls);
    }

    private sealed class FakeAssistant(string answer) : IOpenAiAssistantService
    {
        public int Calls { get; private set; }

        public Task<AssistantResponse> AskAsync(
            string apiKey,
            string model,
            string question,
            string worldSnapshotJson,
            ResponseProfileSettings responseProfile,
            Func<string, System.Text.Json.JsonElement, CancellationToken, Task<string>> executeTool,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AssistantResponse(answer, model, 0, 0, 0));
        }

        public void ResetConversation() { }
        public void Dispose() { }
    }

    private sealed class FakeTurns(AssistantResponse answer) : IAssistantTurnService
    {
        public Task<AssistantResponse> SubmitUserTurnAsync(
            string text,
            UserTurnSource source,
            CancellationToken cancellationToken)
            => Task.FromResult(answer);

        public void ResetConversation() { }
    }

    private sealed class FakeSpeechToText(string transcript) : ISpeechToTextService
    {
        public Task<string> TranscribeAsync(
            IAudioRecording recording,
            string apiKey,
            CancellationToken cancellationToken)
            => Task.FromResult(transcript);
    }

    private sealed class FakeTextToSpeech(int failOnCall = 0) : ITextToSpeechService
    {
        public List<string> Texts { get; } = new();

        public Task<AudioPayload> SynthesizeAsync(
            string text,
            string apiKey,
            string voiceId,
            CancellationToken cancellationToken)
        {
            Texts.Add(text);
            if (failOnCall > 0 && Texts.Count == failOnCall)
                throw new InvalidOperationException("synthetic speech failure");
            return Task.FromResult(new AudioPayload(new byte[] { 1, 2, 3 }, "audio/mpeg"));
        }
    }

    private sealed class FakePlayback : IAudioPlaybackService
    {
        public int Calls { get; private set; }
        public Task PlayAsync(AudioPayload audio, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class Recording : IAudioRecording
    {
        public string FilePath => "memory.wav";
        public TimeSpan Duration => TimeSpan.FromSeconds(1);
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
