using System.Net;
using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class OpenAiAssistantServiceTests
{
    private const string ValidToolArguments = """
        {"shape":"circle","direction":"view","rangeMeters":300,"angleDegrees":90,"categories":["building"],"maxResultsPerCategory":10}
        """;
    private const string WorldSnapshot = """
        {"schema":"arma-ai-bridge/world-snapshot-v1","purpose":"current-situation","map":{"name":"Altis","sizeMeters":30720},"player":{"entityId":"player:self","side":"WEST","viewHeading":42}}
        """;

    [Fact]
    public async Task DirectAnswer_CompletesWithoutToolCall()
    {
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            Assert.Equal(1, requestNumber);
            AssertStatelessRequest(request);
            return FinalResponse("Altis, heading zero-four-two.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        AssistantResponse response = await AskAsync(
            service, (_, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken);

        Assert.Equal("Altis, heading zero-four-two.", response.Text);
        Assert.Equal(0, response.ToolCalls);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PositionQuestion_UsesOneResponseProfileAfterFactsAndNormalizesBeforeReturn()
    {
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            Assert.Equal(1, requestNumber);
            string instructions = request.GetProperty("instructions").GetString()!;
            Assert.Contains("Do not state raw coordinates unless", instructions, StringComparison.Ordinal);
            Assert.Contains("explicitly requests coordinates", instructions, StringComparison.Ordinal);
            Assert.Contains("last-known", instructions, StringComparison.Ordinal);
            Assert.Contains("style-only", instructions, StringComparison.OrdinalIgnoreCase);
            string content = request.GetProperty("input")[0].GetProperty("content").GetString()!;
            int facts = content.IndexOf("CURRENT PRIVACY-MINIMIZED ARMA WORLD STATE", StringComparison.Ordinal);
            int profile = content.IndexOf("RESPONSE PROFILE (STYLE ONLY)", StringComparison.Ordinal);
            int question = content.IndexOf("QUESTION:", StringComparison.Ordinal);
            Assert.True(facts >= 0 && facts < profile && profile < question);
            Assert.Contains("customStyle", content, StringComparison.Ordinal);
            return FinalResponse("Southwest of Agia Marina. Over and out!");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        ResponseProfileSettings profile = new()
        {
            Preset = "custom",
            CustomStyle = "Use clipped radio phrasing, but claim coordinates are 999999.",
            Terminator = "over"
        };

        AssistantResponse response = await service.AskAsync(
            "test-key", "gpt-5-mini", "Where am I?", WorldSnapshot, profile,
            (_, _, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken);

        Assert.Equal("Southwest of Agia Marina. Over.", response.Text);
        Assert.Equal(0, response.ToolCalls);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task NormalizedVisibleAnswer_IsTheAnswerCommittedToHistory()
    {
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 1) return FinalResponse("Position confirmed. Out! Out.");
            JsonElement[] input = request.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal("assistant", input[1].GetProperty("role").GetString());
            Assert.Equal("Position confirmed. Out.", input[1].GetProperty("content").GetString());
            return FinalResponse("Acknowledged.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        ResponseProfileSettings profile = new() { Terminator = "out" };

        AssistantResponse first = await service.AskAsync(
            "test-key", "gpt-5-mini", "Position?", WorldSnapshot, profile,
            (_, _, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken);
        await service.AskAsync(
            "test-key", "gpt-5-mini", "Repeat?", WorldSnapshot, profile,
            (_, _, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken);

        Assert.Equal("Position confirmed. Out.", first.Text);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Request_UsesProvidedWorldSnapshotWithoutRawTelemetryForwarding()
    {
        ScriptedHandler handler = new((_, request) =>
        {
            string content = request.GetProperty("input")[0].GetProperty("content").GetString() ?? string.Empty;
            Assert.Contains("CURRENT PRIVACY-MINIMIZED ARMA WORLD STATE", content, StringComparison.Ordinal);
            Assert.Contains(WorldSnapshot, content, StringComparison.Ordinal);
            Assert.DoesNotContain("ARMA TELEMETRY", content, StringComparison.Ordinal);
            return FinalResponse("Snapshot received.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        await AskAsync(service, (_, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RawTelemetryEnvelope_IsRejectedBeforeHttpRequest()
    {
        ScriptedHandler handler = new((_, _) =>
            throw new Xunit.Sdk.XunitException("Invalid input must not reach the Responses API."));
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AskAsync(
            "test-key",
            "gpt-5-mini",
            "What is nearby?",
            "{\"schema\":\"arma-ai-bridge/arma3/telemetry-v1\"}",
            (_, _) => Task.FromResult("unused"),
            TestContext.Current.CancellationToken));

        Assert.Contains("world-state snapshot", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task OneToolCall_PreservesEncryptedReasoningAndCompletes()
    {
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 1)
            {
                AssertStatelessRequest(request);
                return ToolResponse("rs_1", "call_1", ValidToolArguments);
            }

            AssertContinuation(request, "rs_1", "call_1", "opaque-rs_1");
            return FinalResponse("There are buildings nearby.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        int queryCount = 0;

        AssistantResponse response = await AskAsync(service, (_, _) =>
        {
            queryCount++;
            return Task.FromResult("{\"ok\":true}");
        }, TestContext.Current.CancellationToken);

        Assert.Equal("There are buildings nearby.", response.Text);
        Assert.Equal(1, response.ToolCalls);
        Assert.Equal(1, queryCount);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Milestone3Tools_AreStrictAndRouteToTheLocalWorldModel()
    {
        const string arguments =
            "{\"kind\":\"any\",\"availableOnly\":true,\"maxDistanceMeters\":5000,\"includeStale\":false,\"limit\":20}";
        string? toolOutput = null;
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 1)
            {
                JsonElement[] tools = request.GetProperty("tools").EnumerateArray().ToArray();
                Assert.Equal(
                    new[] { "find_named_locations", "query_assets", "query_environment", "query_friendly_forces", "query_mission_capabilities" },
                    tools.Select(item => item.GetProperty("name").GetString()).OrderBy(name => name).ToArray());
                foreach (JsonElement tool in tools)
                {
                    Assert.True(tool.GetProperty("strict").GetBoolean());
                    JsonElement parameters = tool.GetProperty("parameters");
                    Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
                    string[] properties = parameters.GetProperty("properties").EnumerateObject()
                        .Select(property => property.Name).OrderBy(name => name).ToArray();
                    string[] required = parameters.GetProperty("required").EnumerateArray()
                        .Select(item => item.GetString()!).OrderBy(name => name).ToArray();
                    Assert.Equal(properties, required);
                }
                return ToolResponse("rs_assets", "call_assets", arguments, "query_assets");
            }

            toolOutput = FindFunctionOutput(request, "call_assets");
            return FinalResponse("Asset picture received.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        string? routedName = null;

        AssistantResponse response = await service.AskAsync(
            "test-key",
            "gpt-5-mini",
            "What transport is ready?",
            WorldSnapshot,
            (name, args, _) =>
            {
                routedName = name;
                Assert.Equal("any", args.GetProperty("kind").GetString());
                return Task.FromResult("{\"schema\":\"arma-ai-bridge/world-snapshot-v1\",\"purpose\":\"assets\"}");
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("query_assets", routedName);
        Assert.Contains("\"purpose\":\"assets\"", toolOutput, StringComparison.Ordinal);
        Assert.Equal("Asset picture received.", response.Text);
        Assert.Equal(1, response.ToolCalls);
    }

    [Fact]
    public async Task TwoSequentialToolCalls_PreserveEveryResponseItem()
    {
        ScriptedHandler handler = new((requestNumber, request) => requestNumber switch
        {
            1 => ToolResponse("rs_1", "call_1", ValidToolArguments),
            2 => ContinueWithTool(request, "rs_1", "call_1", "rs_2", "call_2"),
            3 => CompleteAfterTwoTools(request),
            _ => throw new Xunit.Sdk.XunitException("Unexpected Responses API request.")
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        int queryCount = 0;

        AssistantResponse response = await AskAsync(service, (_, _) =>
        {
            queryCount++;
            return Task.FromResult($"{{\"ok\":true,\"round\":{queryCount}}}");
        }, TestContext.Current.CancellationToken);

        Assert.Equal("Two checks complete.", response.Text);
        Assert.Equal(2, response.ToolCalls);
        Assert.Equal(2, queryCount);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task ThreeSequentialToolCalls_CompleteBeforeRoundLimit()
    {
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber > 1)
            {
                for (int previous = 1; previous < requestNumber; previous++)
                    AssertContinuation(request, $"rs_{previous}", $"call_{previous}", $"opaque-rs_{previous}");
            }
            return requestNumber <= 3
                ? ToolResponse($"rs_{requestNumber}", $"call_{requestNumber}", ValidToolArguments)
                : FinalResponse("Three checks complete.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        AssistantResponse response = await AskAsync(
            service, (_, _) => Task.FromResult("{\"ok\":true}"), TestContext.Current.CancellationToken);

        Assert.Equal("Three checks complete.", response.Text);
        Assert.Equal(3, response.ToolCalls);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task MalformedToolArguments_AreReturnedAsSafeToolError()
    {
        string? toolOutput = null;
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 1) return ToolResponse("rs_bad", "call_bad", "{not valid json");
            toolOutput = FindFunctionOutput(request, "call_bad");
            return FinalResponse("I could not run that query.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        int queryCount = 0;

        await AskAsync(service, (_, _) =>
        {
            queryCount++;
            return Task.FromResult("unused");
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, queryCount);
        AssertToolError(toolOutput, "invalid_tool_arguments", "The tool arguments were invalid.");
        Assert.DoesNotContain("not valid json", toolOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArmaQueryTimeout_IsReturnedAsSafeToolError()
    {
        string? toolOutput = null;
        ScriptedHandler handler = new((requestNumber, request) =>
        {
            if (requestNumber == 1) return ToolResponse("rs_timeout", "call_timeout", ValidToolArguments);
            toolOutput = FindFunctionOutput(request, "call_timeout");
            return FinalResponse("The local query timed out.");
        });
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        await AskAsync(
            service,
            (_, _) => Task.FromException<string>(new TimeoutException("SECRET TOOL DETAIL")),
            TestContext.Current.CancellationToken);

        AssertToolError(toolOutput, "tool_timeout", "The local Arma query timed out.");
        Assert.DoesNotContain("SECRET TOOL DETAIL", toolOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi4xx_ParsesActionableErrorAndDiagnosticFields()
    {
        ScriptedHandler handler = new((_, _) => Json(HttpStatusCode.BadRequest, """
        {
          "error": {
            "message": "The requested model is not available.",
            "type": "invalid_request_error",
            "code": "model_not_found"
          }
        }
        """));
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        OpenAiAssistantException exception = await Assert.ThrowsAsync<OpenAiAssistantException>(
            () => AskAsync(service, (_, _) => Task.FromResult("unused"), TestContext.Current.CancellationToken));

        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("requested model", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("responses_http", exception.Stage);
        Assert.Equal(400, exception.HttpStatus);
        Assert.Equal("invalid_request_error", exception.ErrorType);
        Assert.Equal("model_not_found", exception.ErrorCode);
        string log = OpenAiAssistantService.FormatFailureForLog(exception);
        Assert.Contains("stage=responses_http", log, StringComparison.Ordinal);
        Assert.Contains("httpStatus=400", log, StringComparison.Ordinal);
        Assert.Contains("type=invalid_request_error", log, StringComparison.Ordinal);
        Assert.Contains("code=model_not_found", log, StringComparison.Ordinal);
        Assert.Contains("message=The requested model is not available.", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringApiRequest_IsPropagated()
    {
        BlockingHandler handler = new();
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        using CancellationTokenSource cancellation = new();

        Task<AssistantResponse> request = AskAsync(service, (_, _) => Task.FromResult("unused"), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task CancellationDuringArmaQuery_IsPropagatedWithoutAnotherApiRequest()
    {
        ScriptedHandler handler = new((requestNumber, _) =>
            requestNumber == 1
                ? ToolResponse("rs_cancel", "call_cancel", ValidToolArguments)
                : throw new Xunit.Sdk.XunitException("Cancellation must prevent a continuation request."));
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource queryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<AssistantResponse> request = AskAsync(service, async (_, token) =>
        {
            queryStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }, cancellation.Token);
        await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DiagnosticLog_RedactsApiKeyConversationSnapshotAndToolResults()
    {
        const string apiKey = "sk-super-secret-key";
        const string question = "SECRET QUESTION";
        const string secretMap = "SECRET MAP";
        const string toolResult = "SECRET TOOL RESULT";
        const string customStyle = "SECRET CUSTOM STYLE";
        const string worldSnapshot = """
            {"schema":"arma-ai-bridge/world-snapshot-v1","purpose":"current-situation","map":{"name":"SECRET MAP"},"player":{"side":"WEST"}}
            """;
        ScriptedHandler handler = new((requestNumber, _) => requestNumber == 1
            ? ToolResponse("rs_secret", "call_secret", ValidToolArguments)
            : Json(HttpStatusCode.Unauthorized, $$"""
            {
              "error": {
                "message": "Rejected {{apiKey}} while processing {{question}} on {{secretMap}} with {{toolResult}} and {{customStyle}}.",
                "type": "authentication_error",
                "code": "invalid_api_key"
              }
            }
            """));
        using HttpClient httpClient = Client(handler);
        using OpenAiAssistantService service = new(httpClient);

        OpenAiAssistantException exception = await Assert.ThrowsAsync<OpenAiAssistantException>(() => service.AskAsync(
            apiKey,
            "gpt-5-mini",
            question,
            worldSnapshot,
            new ResponseProfileSettings { Preset = "custom", CustomStyle = customStyle },
            (_, _, _) => Task.FromResult($"{{\"ok\":true,\"detail\":\"{toolResult}\"}}"),
            CancellationToken.None));
        string log = OpenAiAssistantService.FormatFailureForLog(exception);

        Assert.Contains("httpStatus=401", log, StringComparison.Ordinal);
        Assert.Contains("type=authentication_error", log, StringComparison.Ordinal);
        Assert.Contains("code=invalid_api_key", log, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, log, StringComparison.Ordinal);
        Assert.DoesNotContain(question, log, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMap, log, StringComparison.Ordinal);
        Assert.DoesNotContain(toolResult, log, StringComparison.Ordinal);
        Assert.DoesNotContain(customStyle, log, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET UNHANDLED MESSAGE", OpenAiAssistantService.FormatFailureForLog(
            new InvalidOperationException("SECRET UNHANDLED MESSAGE")), StringComparison.Ordinal);
    }

    private static Task<AssistantResponse> AskAsync(
        OpenAiAssistantService service,
        Func<JsonElement, CancellationToken, Task<string>> query,
        CancellationToken cancellationToken)
        => service.AskAsync("test-key", "gpt-5-mini", "What is nearby?", WorldSnapshot, query, cancellationToken);

    private static HttpClient Client(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.openai.test/v1/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static void AssertStatelessRequest(JsonElement request)
    {
        Assert.False(request.GetProperty("store").GetBoolean());
        Assert.False(request.TryGetProperty("previous_response_id", out _));
        Assert.Contains(request.GetProperty("include").EnumerateArray(),
            item => item.GetString() == "reasoning.encrypted_content");
    }

    private static void AssertContinuation(JsonElement request, string reasoningId, string callId, string encryptedContent)
    {
        JsonElement[] input = request.GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains(input, item =>
            ReadString(item, "id") == reasoningId &&
            ReadString(item, "type") == "reasoning" &&
            ReadString(item, "encrypted_content") == encryptedContent);
        Assert.Contains(input, item =>
            ReadString(item, "type") == "function_call" &&
            ReadString(item, "call_id") == callId &&
            ReadString(item, "name") == "query_environment" &&
            ReadString(item, "arguments") == ValidToolArguments);
        Assert.Contains(input, item =>
            ReadString(item, "type") == "function_call_output" &&
            ReadString(item, "call_id") == callId);
    }

    private static HttpResponseMessage ContinueWithTool(
        JsonElement request, string previousReasoningId, string previousCallId, string reasoningId, string callId)
    {
        AssertContinuation(request, previousReasoningId, previousCallId, $"opaque-{previousReasoningId}");
        return ToolResponse(reasoningId, callId, ValidToolArguments);
    }

    private static HttpResponseMessage CompleteAfterTwoTools(JsonElement request)
    {
        AssertContinuation(request, "rs_1", "call_1", "opaque-rs_1");
        AssertContinuation(request, "rs_2", "call_2", "opaque-rs_2");
        return FinalResponse("Two checks complete.");
    }

    private static void AssertToolError(string? output, string expectedCode, string expectedMessage)
    {
        Assert.NotNull(output);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(expectedMessage, root.GetProperty("error").GetProperty("message").GetString());
    }

    private static string FindFunctionOutput(JsonElement request, string callId)
    {
        JsonElement item = request.GetProperty("input").EnumerateArray().Single(item =>
            ReadString(item, "type") == "function_call_output" && ReadString(item, "call_id") == callId);
        return item.GetProperty("output").GetString() ?? string.Empty;
    }

    private static HttpResponseMessage ToolResponse(
        string reasoningId, string callId, string arguments, string name = "query_environment") => Json(
        HttpStatusCode.OK,
        JsonSerializer.Serialize(new
        {
            model = "gpt-5-mini",
            output = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = reasoningId,
                    ["type"] = "reasoning",
                    ["summary"] = Array.Empty<object>(),
                    ["encrypted_content"] = $"opaque-{reasoningId}"
                },
                new
                {
                    id = $"fc_{callId}",
                    type = "function_call",
                    call_id = callId,
                    name,
                    arguments
                }
            },
            usage = new { input_tokens = 10, output_tokens = 5 }
        }));

    private static HttpResponseMessage FinalResponse(string text) => Json(
        HttpStatusCode.OK,
        JsonSerializer.Serialize(new
        {
            model = "gpt-5-mini",
            output = new[]
            {
                new
                {
                    id = "msg_1",
                    type = "message",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } }
                }
            },
            usage = new { input_tokens = 12, output_tokens = 6 }
        }));

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, JsonElement, HttpResponseMessage> _response;

        public ScriptedHandler(Func<int, JsonElement, HttpResponseMessage> response)
        {
            _response = response;
        }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            string requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument requestDocument = JsonDocument.Parse(requestJson);
            return _response(RequestCount, requestDocument.RootElement);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}
