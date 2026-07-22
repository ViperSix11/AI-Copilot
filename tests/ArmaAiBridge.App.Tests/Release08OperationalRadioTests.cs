using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class Release08OperationalRadioTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlayerGroupCallsign_IsCollectedParsedAndStoredExactly()
    {
        string root = RepositoryRoot();
        string sqf = File.ReadAllText(Path.Combine(root,
            "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishStateSnapshot.sqf"));
        Assert.Contains("[\"groupCallsign\", groupId (group player)]", sqf, StringComparison.Ordinal);
        string schema = File.ReadAllText(Path.Combine(root, "schemas/state-snapshot-v2.schema.json"));
        Assert.Contains("\"groupCallsign\"", schema, StringComparison.Ordinal);

        using TempDatabase database = new();
        using SqliteStateRepository repository = new(database.Path, new ManualTimeProvider(Start));
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplyHandshake(
            "stratis/state-mirror-acceptance", "session-fixture-1", "Stratis", 8192, Start).Status);
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(SnapshotNode())).Status);

        Assert.Equal("Alpha 1-1", repository.GetPlayer()!.GroupCallsign);
    }

    [Fact]
    public void GroupAndSessionChanges_DoNotRetainAnOldCallsign()
    {
        using TempDatabase database = new();
        using SqliteStateRepository repository = new(database.Path, new ManualTimeProvider(Start));
        repository.ApplyHandshake("stratis/state-mirror-acceptance", "session-fixture-1", "Stratis", 8192, Start);
        repository.ApplySnapshot(Parse(SnapshotNode()));

        JsonNode changed = SnapshotNode();
        changed["messageId"] = "message-43";
        changed["sequence"] = 43;
        changed["timestamp"] = 41;
        changed["sections"]!["player"]!["groupSourceId"] = "net:44:1";
        changed["sections"]!["player"]!["groupCallsign"] = "Bravo 2-3";
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(changed)).Status);
        Assert.Equal("Bravo 2-3", repository.GetPlayer()!.GroupCallsign);
        RadioAcknowledgementService acknowledgements = new();
        Assert.Contains("Bravo 2-3", acknowledgements.Create(repository.GetPlayer()!.GroupCallsign).VisibleText,
            StringComparison.Ordinal);

        repository.ApplyHandshake("altis/new-mission", "session-fixture-2", "Altis", 30720, Start.AddMinutes(1));
        Assert.Null(repository.GetPlayer());

        changed["missionId"] = "altis/new-mission";
        changed["sessionId"] = "session-fixture-2";
        changed["messageId"] = "message-new-1";
        changed["sequence"] = 1;
        changed["timestamp"] = 50;
        changed["sections"]!["player"]!["groupCallsign"] = "Charlie 4-1";
        Assert.Equal(TelemetryIngestStatus.Applied, repository.ApplySnapshot(Parse(changed)).Status);
        Assert.Equal("Charlie 4-1", repository.GetPlayer()!.GroupCallsign);
        Assert.Contains("Charlie 4-1", acknowledgements.Create(repository.GetPlayer()!.GroupCallsign).VisibleText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Acknowledgements_UseOnlyCurrentCallsignEnglishTemplatesAndNoConsecutiveRepeat()
    {
        RadioAcknowledgementService service = new();
        List<RadioAcknowledgement> acknowledgements = Enumerable.Range(0, 16)
            .Select(_ => service.Create("Alpha 1-1"))
            .ToList();

        Assert.Equal(8, RadioAcknowledgementService.Templates.Count);
        Assert.All(RadioAcknowledgementService.Templates, template =>
        {
            Assert.Contains("{0}", template, StringComparison.Ordinal);
            Assert.DoesNotContain("warte", template, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verstanden", template, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prüf", template, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(acknowledgements, item =>
        {
            Assert.Equal("Alpha 1-1", item.GroupCallsign);
            Assert.Contains("Alpha 1-1", item.VisibleText, StringComparison.Ordinal);
        });
        for (int index = 1; index < acknowledgements.Count; index++)
            Assert.NotEqual(acknowledgements[index - 1].VariationId, acknowledgements[index].VariationId);
    }

    [Fact]
    public void MissingCallsign_UsesExactNeutralFallbackAndNeverLeaksAnIdentifier()
    {
        RadioAcknowledgement acknowledgement = new RadioAcknowledgementService().Create("   ");
        Assert.Equal("Papa Bear copies. Stand by.", acknowledgement.VisibleText);
        Assert.Equal(acknowledgement.VisibleText, acknowledgement.SpokenText);
        Assert.Equal(string.Empty, acknowledgement.GroupCallsign);
        Assert.DoesNotContain("source", acknowledgement.VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alias", acknowledgement.VisibleText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Papa Bear. Current picture follows.",
            RadioFinalResponsePolicy.EnsureCurrentCallsign("Papa Bear. Current picture follows.", string.Empty));
    }

    [Fact]
    public void SpeechFormatting_PreservesStoredIdentityAndChangesOnlySpeechText()
    {
        const string callsign = "Alpha 1-1";
        RadioAcknowledgement acknowledgement = new RadioAcknowledgementService().Create(callsign);
        Assert.Equal(callsign, acknowledgement.GroupCallsign);
        Assert.Contains(callsign, acknowledgement.VisibleText, StringComparison.Ordinal);
        Assert.Contains("Alpha One-One", acknowledgement.SpokenText, StringComparison.Ordinal);
        Assert.Equal(
            "Alpha One-One, Papa Bear. Wind is northwest.",
            CallsignSpeechFormatter.FormatAnswerForSpeech(
                "Alpha 1-1, Papa Bear. Wind is northwest.", callsign));
        Assert.Equal(callsign, acknowledgement.GroupCallsign);
    }

    [Fact]
    public async Task CurrentCallsign_ReachesFinalModelPromptAndOverridesHistorySemantics()
    {
        CapturingHandler handler = new((_, request) =>
        {
            string instructions = request.GetProperty("instructions").GetString()!;
            Assert.Contains("exact current callsign", instructions, StringComparison.Ordinal);
            Assert.Contains("earlier conversation history", instructions, StringComparison.Ordinal);
            Assert.Contains("omit direct callsign address", instructions, StringComparison.Ordinal);
            string content = request.GetProperty("input").EnumerateArray().Last().GetProperty("content").GetString()!;
            Assert.Contains("\"groupCallsign\":\"Alpha 1-1\"", content, StringComparison.Ordinal);
            return Task.FromResult(FinalResponse("Papa Bear. Copy."));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using AssistantTurnService turns = new(
            new OpenAiAssistantService(client),
            _ => (true, """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}"""),
            _ => Task.FromResult(("test-key", "gpt-5-mini", new ResponseProfileSettings())),
            (_, _, _) => Task.FromResult("unused"),
            acknowledgementDelay: TimeSpan.Zero);

        AssistantResponse response = await turns.SubmitUserTurnAsync(
            "What is my position?", UserTurnSource.Typed, _ => { },
            TestContext.Current.CancellationToken);

        Assert.Equal("Alpha 1-1, Papa Bear. Copy.", response.Text);
        Assert.Equal("Alpha 1-1", response.GroupCallsign);
        Assert.Equal(0, response.RequestMetrics!.SelectedToolCount);
    }

    [Fact]
    public async Task Acknowledgement_IsDelayedUntilThresholdAndNeverAddedToModelHistory()
    {
        TaskCompletionSource<HttpResponseMessage> firstResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 1) return firstResponse.Task;
            foreach (JsonElement message in request.GetProperty("input").EnumerateArray())
            {
                if (message.TryGetProperty("content", out JsonElement content))
                    Assert.DoesNotContain("Stand by", content.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            return Task.FromResult(FinalResponse("Second final answer."));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using AssistantTurnService turns = new(
            new OpenAiAssistantService(client),
            _ => (true, """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}"""),
            _ => Task.FromResult(("test-key", "gpt-5-mini", new ResponseProfileSettings())),
            (_, _, _) => Task.FromResult("unused"),
            acknowledgementWait: (duration, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(5), duration);
                return Task.CompletedTask;
            });
        TaskCompletionSource<RadioAcknowledgement> acknowledgementReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<AssistantResponse> first = turns.SubmitUserTurnAsync(
            "What is my position?", UserTurnSource.Typed,
            acknowledgement => acknowledgementReady.TrySetResult(acknowledgement),
            TestContext.Current.CancellationToken);
        RadioAcknowledgement acknowledgement = await acknowledgementReady.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Contains("Alpha 1-1", acknowledgement.VisibleText, StringComparison.Ordinal);
        Assert.False(first.IsCompleted);
        firstResponse.SetResult(FinalResponse("First final answer."));
        await first;

        await turns.SubmitUserTurnAsync(
            "What is my loadout?", UserTurnSource.Typed, _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FastAnswerSuppressesAcknowledgementAndReportsThresholdMetrics()
    {
        CapturingHandler handler = new((_, _) => Task.FromResult(FinalResponse("Current picture follows.")));
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using AssistantTurnService turns = new(
            new OpenAiAssistantService(client),
            _ => (true, """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}"""),
            _ => Task.FromResult(("test-key", "gpt-5-mini", new ResponseProfileSettings())),
            (_, _, _) => Task.FromResult("unused"));
        int acknowledgements = 0;

        AssistantResponse answer = await turns.SubmitUserTurnAsync(
            "Situation?", UserTurnSource.Typed, _ => acknowledgements++,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, acknowledgements);
        Assert.True(answer.RequestMetrics!.AcknowledgementEligible);
        Assert.False(answer.RequestMetrics.AcknowledgementEmitted);
        Assert.Equal(5000, answer.RequestMetrics.AcknowledgementThresholdMilliseconds);
        Assert.True(answer.RequestMetrics.AnswerTextLatencyMilliseconds >= 0);
    }

    [Fact]
    public async Task AnswerDuringAcknowledgementPreparationCancelsItBeforePlayback()
    {
        TaskCompletionSource<HttpResponseMessage> responseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingHandler handler = new((_, _) => responseGate.Task);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using AssistantTurnService turns = new(
            new OpenAiAssistantService(client),
            _ => (true, """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}"""),
            _ => Task.FromResult(("test-key", "gpt-5-mini", new ResponseProfileSettings())),
            (_, _, _) => Task.FromResult("unused"),
            acknowledgementDelay: TimeSpan.Zero);
        TaskCompletionSource preparationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool playbackStarted = false;

        Task<AssistantResponse> turn = turns.SubmitUserTurnAsync(
            "Situation?",
            UserTurnSource.Typed,
            async (_, preparationToken) =>
            {
                preparationStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, preparationToken);
                playbackStarted = true;
            },
            null,
            TestContext.Current.CancellationToken);
        await preparationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        responseGate.SetResult(FinalResponse("Current picture follows."));

        AssistantResponse response = await turn;
        Assert.False(playbackStarted);
        Assert.True(response.RequestMetrics!.AcknowledgementEmitted);
    }

    [Fact]
    public async Task StartedAcknowledgementPlaybackFinishesBeforeTurnReturnsForFinalSpeech()
    {
        TaskCompletionSource<HttpResponseMessage> responseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource playbackStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource playbackFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingHandler handler = new((_, _) => responseGate.Task);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using AssistantTurnService turns = new(
            new OpenAiAssistantService(client),
            _ => (true, """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}"""),
            _ => Task.FromResult(("test-key", "gpt-5-mini", new ResponseProfileSettings())),
            (_, _, _) => Task.FromResult("unused"),
            acknowledgementDelay: TimeSpan.Zero);

        Task<AssistantResponse> turn = turns.SubmitUserTurnAsync(
            "Situation?",
            UserTurnSource.Typed,
            async (_, _) =>
            {
                playbackStarted.SetResult();
                await playbackFinished.Task;
            },
            null,
            TestContext.Current.CancellationToken);
        await playbackStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        responseGate.SetResult(FinalResponse("Current picture follows."));
        await Task.Yield();
        Assert.False(turn.IsCompleted);
        playbackFinished.SetResult();
        await turn;
    }

    [Fact]
    public async Task TurnFreezesSnapshotAndCallsignBeforeTheProviderRequestCompletes()
    {
        string snapshot = """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}""";
        TaskCompletionSource<HttpResponseMessage> responseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CapturingHandler handler = new((_, request) =>
        {
            string content = request.GetProperty("input").EnumerateArray().Last().GetProperty("content").GetString()!;
            Assert.Contains("Alpha 1-1", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Bravo 2-3", content, StringComparison.Ordinal);
            return responseGate.Task;
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using AssistantTurnService turns = new(
            new OpenAiAssistantService(client),
            _ => (true, snapshot),
            _ => Task.FromResult(("test-key", "gpt-5-mini", new ResponseProfileSettings())),
            (_, _, _) => Task.FromResult("unused"),
            acknowledgementDelay: TimeSpan.Zero);
        RadioAcknowledgement? acknowledgement = null;

        Task<AssistantResponse> turn = turns.SubmitUserTurnAsync(
            "Situation?", UserTurnSource.Typed, value => acknowledgement = value,
            TestContext.Current.CancellationToken);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        snapshot = """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Bravo 2-3"}}""";
        responseGate.SetResult(FinalResponse("Alpha 1-1, current picture follows."));
        AssistantResponse answer = await turn;

        Assert.Equal("Alpha 1-1", acknowledgement!.GroupCallsign);
        Assert.Equal("Alpha 1-1", answer.GroupCallsign);
    }

    [Fact]
    public async Task HistoryIsAtMostThreePairsFourThousandCharactersAndContainsNoPriorStateBlock()
    {
        CapturingHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 5)
            {
                JsonElement[] input = request.GetProperty("input").EnumerateArray().ToArray();
                JsonElement[] history = input[..^1];
                Assert.True(history.Length <= 6);
                Assert.True(history.Sum(item => item.GetProperty("content").GetString()!.Length) <= 4000);
                Assert.All(history, item => Assert.DoesNotContain(
                    OperationalSnapshotBuilder.Schema,
                    item.GetProperty("content").GetString()!,
                    StringComparison.Ordinal));
                Assert.Contains(OperationalSnapshotBuilder.Schema,
                    input[^1].GetProperty("content").GetString()!, StringComparison.Ordinal);
            }
            return Task.FromResult(FinalResponse(new string('A', 900)));
        });
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.openai.test/v1/") };
        using OpenAiAssistantService service = new(client);
        for (int index = 0; index < 5; index++)
        {
            await service.AskAsync(
                "test-key", "gpt-5-mini", $"Operational question {index}: {new string('Q', 850)}",
                """{"schema":"arma-ai-bridge/operational-snapshot-v1","player":{"groupCallsign":"Alpha 1-1"}}""",
                (_, _, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken);
        }
        Assert.Equal(5, handler.RequestCount);
    }

    [Fact]
    public void ActiveRuntimeContainsNoHardcodedCallsignIdentity()
    {
        string root = RepositoryRoot();
        IEnumerable<string> files = Directory.EnumerateFiles(Path.Combine(root, "src/ArmaAiBridge.App"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "arma3/addon-source"), "*.sqf", SearchOption.AllDirectories));
        string runtime = string.Join('\n', files.Select(File.ReadAllText));
        foreach (string forbidden in new[] { "Viper Six", "Viper 6", "Alpha 1-1" })
            Assert.DoesNotContain(forbidden, runtime, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode SnapshotNode()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "state-snapshot-v2.json")))!;

    private static StateSnapshotMessage Parse(JsonNode node)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        return StateSnapshotParser.Parse(document.RootElement, Start);
    }

    private static HttpResponseMessage FinalResponse(string text)
        => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            status = "completed",
            model = "gpt-5-mini",
            output = new[] { new { type = "message", status = "completed", role = "assistant", content = new[] { new { type = "output_text", text } } } },
            usage = new { input_tokens = 10, output_tokens = 5, output_tokens_details = new { reasoning_tokens = 1 } }
        }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string RepositoryRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<int, JsonElement, Task<HttpResponseMessage>> _handler;
        private int _requests;
        public CapturingHandler(Func<int, JsonElement, Task<HttpResponseMessage>> handler) => _handler = handler;
        public int RequestCount => Volatile.Read(ref _requests);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int number = Interlocked.Increment(ref _requests);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            Started.TrySetResult();
            return await _handler(number, document.RootElement.Clone());
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arma-ai-callsign-tests", Guid.NewGuid().ToString("N"));
        public TempDatabase() { Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "state.sqlite3"); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
