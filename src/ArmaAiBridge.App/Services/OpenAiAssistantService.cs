using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class OpenAiAssistantService : IDisposable
{
    private const string EncryptedReasoningInclude = "reasoning.encrypted_content";
    private const string Instructions = """
You are ArmA AI Bridge, a concise tactical assistant for the local player in Arma 3.
Answer in the user's language, use metric units, and distinguish measured facts from tactical interpretation.
Treat the supplied provenance-aware world-state snapshot as the only current game state. Respect freshness, confidence, and position uncertainty. Never invent map objects, contacts, positions, visibility, routes, or threats.
Use query_environment whenever a question depends on actual terrain objects. Use a circle for the general vicinity and a cone for ahead/in-view questions.
Use query_friendly_forces for detailed own-side group, unit, or vehicle facts; query_assets for support-asset readiness; and query_mission_capabilities for mission-declared read-only capabilities. These tools never execute support.
Use find_named_locations only for official cartographic names. Use query_operational_memory for objects and contacts actually learned from observations; a missing object is unknown, not absent.
Call record_player_observation only when the current user explicitly reports something they presently or previously observed. Never call it for a question, possibility, hypothetical, assumption, recommendation, or fact inferred by you. Copy sourceQuote exactly from the current user turn and do not invent range, bearing, age, or a named location.
Call correct_player_observation only for the user's explicit correction or retraction of a player observation. These controlled writes affect local memory only and never execute anything in Arma.
Choose context-aware ranges: about 300 m on foot, 800 m in a ground vehicle, up to 1500 m in aircraft; respect explicit distances within tool limits.
Only discuss contacts present in player/group knowledge or current vehicle sensors. Never infer hidden enemies from terrain data.
Keep ordinary answers to a few sentences unless more detail is requested.
""";

    private static readonly object[] Tools =
    {
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = "query_environment",
            ["description"] = "Query actual buildings, vegetation, roads, walls or rocks on the loaded Arma 3 map around the player or in a directional cone.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["shape"] = Schema("string", enums: new[] { "circle", "cone" }),
                    ["direction"] = Schema("string", enums: new[] { "view", "body" }),
                    ["rangeMeters"] = Schema("number", 25, 1500),
                    ["angleDegrees"] = Schema("number", 5, 180),
                    ["categories"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 5,
                        ["items"] = Schema("string", enums: new[] { "building", "vegetation", "road", "wall", "rock" })
                    },
                    ["maxResultsPerCategory"] = Schema("integer", 1, 50)
                },
                ["required"] = new[] { "shape", "direction", "rangeMeters", "angleDegrees", "categories", "maxResultsPerCategory" },
                ["additionalProperties"] = false
            }
        },
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = "query_friendly_forces",
            ["description"] = "Read a bounded privacy-safe snapshot of own-side groups, units, or vehicles from the local world model.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["entityType"] = Schema("string", enums: new[] { "group", "unit", "vehicle", "all" }),
                    ["maxDistanceMeters"] = Schema("number", 100, 50000),
                    ["includeStale"] = Schema("boolean"),
                    ["limit"] = Schema("integer", 1, 100)
                },
                ["required"] = new[] { "entityType", "maxDistanceMeters", "includeStale", "limit" },
                ["additionalProperties"] = false
            }
        },
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = "query_assets",
            ["description"] = "Read bounded mission-declared support-asset status from the local world model without executing support.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["kind"] = Schema("string", enums: new[] { "any", "rotary_transport", "ground_transport", "medevac", "resupply", "reconnaissance", "vehicle_recovery", "other" }),
                    ["availableOnly"] = Schema("boolean"),
                    ["maxDistanceMeters"] = Schema("number", 100, 50000),
                    ["includeStale"] = Schema("boolean"),
                    ["limit"] = Schema("integer", 1, 100)
                },
                ["required"] = new[] { "kind", "availableOnly", "maxDistanceMeters", "includeStale", "limit" },
                ["additionalProperties"] = false
            }
        },
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = "query_mission_capabilities",
            ["description"] = "Read the typed mission capability registry from the local world model. No action is available.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["enabledOnly"] = Schema("boolean"),
                    ["includeStale"] = Schema("boolean")
                },
                ["required"] = new[] { "enabledOnly", "includeStale" },
                ["additionalProperties"] = false
            }
        },
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = "find_named_locations",
            ["description"] = "Find official named locations from the small local terrain gazetteer. This does not search buildings, roads, or other map objects.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["query"] = Schema("string"),
                    ["maxDistanceMeters"] = Schema("number", 100, 50000),
                    ["limit"] = Schema("integer", 1, 50)
                },
                ["required"] = new[] { "query", "maxDistanceMeters", "limit" },
                ["additionalProperties"] = false
            }
        },
        new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = "query_operational_memory",
            ["description"] = "Read bounded observation-derived operational entities with provenance, freshness, confidence, uncertainty, and conflicts.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["entityKind"] = Schema("string", enums: new[] { "any", "contact", "vehicle", "supply", "weapon", "fortification", "static", "other" }),
                    ["maxDistanceMeters"] = Schema("number", 100, 50000),
                    ["freshness"] = Schema("string", enums: new[] { "live", "recent", "stale", "historical", "any" }),
                    ["includeConflicts"] = Schema("boolean"),
                    ["limit"] = Schema("integer", 1, 100)
                },
                ["required"] = new[] { "entityKind", "maxDistanceMeters", "freshness", "includeConflicts", "limit" },
                ["additionalProperties"] = false
            }
        },
        PlayerObservationTool("record_player_observation", correction: false),
        PlayerObservationTool("correct_player_observation", correction: true)
    };

    private static Dictionary<string, object?> PlayerObservationTool(string name, bool correction)
    {
        Dictionary<string, object?> properties = new()
        {
            ["sourceQuote"] = Schema("string"),
            ["timeReference"] = Schema("string", enums: new[] { "present", "past" }),
            ["entityKind"] = Schema("string", enums: new[] { "contact", "vehicle", "supply", "weapon", "fortification", "static", "other" }),
            ["classification"] = Schema("string", enums: new[]
            {
                "unknown", "infantry", "vehicle", "car", "truck", "offroad", "tank", "apc",
                "aircraft", "helicopter", "boat", "crate", "ammo", "weapon", "fortification", "static"
            }),
            ["state"] = Schema("string", enums: new[] { "intact", "damaged", "destroyed", "changed", "unknown" }),
            ["rangeMeters"] = NullableNumber(1, 50000),
            ["bearingDegrees"] = NullableNumber(-360, 360),
            ["bearingReference"] = Schema("string", enums: new[] { "absolute", "view", "body" }),
            ["rangePrecisionMeters"] = Schema("number", 1, 5000),
            ["bearingPrecisionDegrees"] = Schema("number", 1, 90),
            ["ageSeconds"] = NullableNumber(0, 86400),
            ["namedLocation"] = new Dictionary<string, object?> { ["type"] = new[] { "string", "null" } }
        };
        List<string> required = properties.Keys.ToList();
        if (correction)
        {
            properties["observationAlias"] = Schema("string");
            properties["action"] = Schema("string", enums: new[] { "correct", "retract" });
            required.Add("observationAlias");
            required.Add("action");
        }
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = name,
            ["description"] = correction
                ? "Correct or retract only an explicit player-report observation named by its local alias. Copy the correction quote exactly from the current turn."
                : "Record only an explicit present or past player report into local operational memory. Copy sourceQuote exactly; never use for questions or assumptions.",
            ["strict"] = true,
            ["parameters"] = new Dictionary<string, object?>
            {
                ["type"] = "object", ["properties"] = properties,
                ["required"] = required.ToArray(), ["additionalProperties"] = false
            }
        };
    }

    private static Dictionary<string, object?> NullableNumber(double minimum, double maximum) => new()
    {
        ["type"] = new[] { "number", "null" }, ["minimum"] = minimum, ["maximum"] = maximum
    };

    private static readonly HashSet<string> AllowedToolNames = new(StringComparer.Ordinal)
    {
        "query_environment", "query_friendly_forces", "query_assets", "query_mission_capabilities",
        "find_named_locations", "query_operational_memory", "record_player_observation", "correct_player_observation"
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly List<(string Role, string Text)> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public OpenAiAssistantService()
        : this(new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/v1/"),
            Timeout = TimeSpan.FromSeconds(60)
        }, ownsHttpClient: true)
    {
    }

    public OpenAiAssistantService(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private OpenAiAssistantService(HttpClient httpClient, bool ownsHttpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public Task<AssistantResponse> AskAsync(
        string apiKey, string model, string question, string worldSnapshotJson,
        Func<JsonElement, CancellationToken, Task<string>> queryEnvironment,
        CancellationToken cancellationToken)
        => AskAsync(
            apiKey, model, question, worldSnapshotJson,
            (name, arguments, token) => name == "query_environment"
                ? queryEnvironment(arguments, token)
                : Task.FromException<string>(new InvalidOperationException("The requested local tool is unavailable.")),
            cancellationToken);

    public async Task<AssistantResponse> AskAsync(
        string apiKey, string model, string question, string worldSnapshotJson,
        Func<string, JsonElement, CancellationToken, Task<string>> executeTool,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Save an OpenAI API key first.");
        if (string.IsNullOrWhiteSpace(question)) throw new InvalidOperationException("Enter a question first.");
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string worldSnapshot = ValidateWorldSnapshot(worldSnapshotJson);
            List<object> input = _history.Select(x => (object)Message(x.Role, x.Text)).ToList();
            input.Add(Message("user", $"CURRENT PRIVACY-MINIMIZED ARMA WORLD STATE:\n{worldSnapshot}\n\nQUESTION:\n{question.Trim()}"));
            HashSet<string> sensitiveValues = new(StringComparer.Ordinal)
            {
                apiKey.Trim(),
                question.Trim(),
                worldSnapshot
            };
            foreach ((string _, string text) in _history) AddSensitiveValue(sensitiveValues, text);
            AddSensitiveJsonStrings(sensitiveValues, worldSnapshot);
            int toolCalls = 0, inputTokens = 0, outputTokens = 0;
            string effectiveModel = string.IsNullOrWhiteSpace(model) ? "gpt-5-mini" : model.Trim();

            for (int round = 0; round < 4; round++)
            {
                using JsonDocument response = await PostAsync(
                    apiKey.Trim(), effectiveModel, input, sensitiveValues, cancellationToken).ConfigureAwait(false);
                JsonElement root = response.RootElement;
                effectiveModel = ReadString(root, "model", effectiveModel);
                AddUsage(root, ref inputTokens, ref outputTokens);
                if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
                    throw Failure("OpenAI returned an invalid response. Try again.", "response_parse", "Missing output array.");

                List<JsonElement> items = output.EnumerateArray().Select(x => x.Clone()).ToList();
                foreach (JsonElement item in items) AddSensitiveJsonStrings(sensitiveValues, item);
                List<JsonElement> calls = items.Where(x => ReadString(x, "type") == "function_call").ToList();
                if (calls.Count == 0)
                {
                    string answer = ExtractText(items);
                    if (answer.Length == 0)
                        throw Failure("OpenAI returned no answer. Try again.", "response_parse", "Missing final output text.");
                    AddHistory(question.Trim(), answer);
                    return new AssistantResponse(answer, effectiveModel, toolCalls, inputTokens, outputTokens);
                }
                if (round == 3)
                    throw Failure("OpenAI exceeded three local tool rounds. Try a more specific question.", "tool_loop", "Tool round limit exceeded.");
                foreach (JsonElement item in items) input.Add(item);
                foreach (JsonElement call in calls)
                {
                    toolCalls++;
                    string callId = ReadString(call, "call_id");
                    string name = ReadString(call, "name");
                    if (callId.Length == 0)
                        throw Failure("OpenAI returned an invalid tool call. Try again.", "response_parse", "Function call is missing call_id.");
                    string result;
                    try
                    {
                        if (!AllowedToolNames.Contains(name))
                        {
                            result = ToolError("unsupported_tool", "The requested tool is not available.");
                        }
                        else
                        {
                            using JsonDocument args = JsonDocument.Parse(ReadString(call, "arguments"));
                            result = await executeTool(name, args.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (JsonException)
                    {
                        result = ToolError("invalid_tool_arguments", "The tool arguments were invalid.");
                    }
                    catch (TimeoutException)
                    {
                        result = ToolError("tool_timeout", "The local Arma query timed out.");
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        result = ToolError("tool_failed", "The local Arma query could not be completed.");
                    }
                    AddSensitiveValue(sensitiveValues, result);
                    AddSensitiveJsonStrings(sensitiveValues, result);
                    input.Add(FunctionOutput(callId, result));
                }
            }
            throw Failure("The assistant tool loop did not complete. Try again.", "tool_loop", "Tool loop ended unexpectedly.");
        }
        finally { _lock.Release(); }
    }

    public void ResetConversation() => _history.Clear();

    private async Task<JsonDocument> PostAsync(
        string apiKey,
        string model,
        IReadOnlyList<object> input,
        IReadOnlySet<string> sensitiveValues,
        CancellationToken token)
    {
        Dictionary<string, object?> body = new()
        {
            ["model"] = model, ["instructions"] = Instructions, ["input"] = input, ["tools"] = Tools,
            ["tool_choice"] = "auto", ["parallel_tool_calls"] = false, ["max_output_tokens"] = 600, ["store"] = false,
            // Manage context locally: request opaque reasoning and replay every output item unchanged.
            ["include"] = new[] { EncryptedReasoningInclude }
        };
        using HttpRequestMessage request = new(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("ArmA-AI-Bridge/0.3.0");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw Failure(
                "Could not reach OpenAI. Check the network connection and try again.",
                "responses_send",
                exception.GetType().Name,
                innerException: exception);
        }
        using (response)
        {
            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ApiError error = ReadApiError(json, apiKey, sensitiveValues);
                throw Failure(
                    $"OpenAI rejected the request ({(int)response.StatusCode}): {error.Message}",
                    "responses_http",
                    error.Message,
                    (int)response.StatusCode,
                    error.Type,
                    error.Code);
            }
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                throw Failure(
                    "OpenAI returned an invalid response. Try again.",
                    "response_parse",
                    "Response body was not valid JSON.",
                    (int)response.StatusCode,
                    innerException: exception);
            }
        }
    }

    private static string ValidateWorldSnapshot(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                ReadString(root, "schema") != WorldSnapshotBuilder.SnapshotSchema)
            {
                throw new InvalidOperationException("The local world-state snapshot is invalid.");
            }
            return root.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The local world-state snapshot is invalid.", exception);
        }
    }

    private void AddHistory(string question, string answer)
    {
        _history.Add(("user", Truncate(question))); _history.Add(("assistant", Truncate(answer)));
        while (_history.Count > 12) _history.RemoveAt(0);
    }
    private static string Truncate(string value) => value.Length <= 4000 ? value : value[..4000];
    private static Dictionary<string, object?> Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
    private static Dictionary<string, object?> FunctionOutput(string callId, string output) => new()
    {
        ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output
    };
    private static string ToolError(string code, string message)
        => JsonSerializer.Serialize(new { ok = false, error = new { code, message } });
    private static Dictionary<string, object?> Schema(string type, double? min = null, double? max = null, string[]? enums = null)
    {
        Dictionary<string, object?> schema = new() { ["type"] = type };
        if (min.HasValue) schema["minimum"] = min.Value; if (max.HasValue) schema["maximum"] = max.Value; if (enums is not null) schema["enum"] = enums;
        return schema;
    }
    private static string ExtractText(IEnumerable<JsonElement> items) => string.Join("\n", items
        .Where(x => ReadString(x, "type") == "message" && x.TryGetProperty("content", out _))
        .SelectMany(x => x.GetProperty("content").EnumerateArray())
        .Where(x => ReadString(x, "type") == "output_text")
        .Select(x => ReadString(x, "text")).Where(x => x.Length > 0)).Trim();
    private static void AddUsage(JsonElement root, ref int input, ref int output)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage)) return;
        input += ReadInt(usage, "input_tokens"); output += ReadInt(usage, "output_tokens");
    }
    private static ApiError ReadApiError(string json, string apiKey, IReadOnlySet<string> sensitiveValues)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.Object)
                return new ApiError("Request rejected.", string.Empty, string.Empty);
            return new ApiError(
                SanitizeDiagnostic(ReadString(error, "message", "Request rejected."), apiKey, sensitiveValues),
                SanitizeField(ReadString(error, "type")),
                SanitizeField(ReadString(error, "code")));
        }
        catch (JsonException)
        {
            return new ApiError("Request rejected.", string.Empty, string.Empty);
        }
    }

    public static string FormatFailureForLog(Exception exception)
    {
        if (exception is not OpenAiAssistantException failure)
            return $"OpenAI assistant failed: stage=unhandled, exceptionType={exception.GetType().Name}.";

        List<string> fields = new() { $"stage={SanitizeField(failure.Stage)}" };
        if (failure.HttpStatus.HasValue) fields.Add($"httpStatus={failure.HttpStatus.Value}");
        if (!string.IsNullOrWhiteSpace(failure.ErrorType)) fields.Add($"type={SanitizeField(failure.ErrorType)}");
        if (!string.IsNullOrWhiteSpace(failure.ErrorCode)) fields.Add($"code={SanitizeField(failure.ErrorCode)}");
        if (!string.IsNullOrWhiteSpace(failure.DiagnosticMessage)) fields.Add($"message={SanitizeField(failure.DiagnosticMessage)}");
        return $"OpenAI assistant failed: {string.Join(", ", fields)}.";
    }

    private static OpenAiAssistantException Failure(
        string userMessage,
        string stage,
        string diagnosticMessage,
        int? httpStatus = null,
        string? errorType = null,
        string? errorCode = null,
        Exception? innerException = null)
        => new(userMessage, stage, httpStatus, errorType, errorCode, SanitizeField(diagnosticMessage), innerException);

    private static string SanitizeDiagnostic(string message, string apiKey, IReadOnlySet<string> sensitiveValues)
    {
        string sanitized = message;
        foreach (string value in sensitiveValues.OrderByDescending(value => value.Length))
        {
            if (value.Length >= 4) sanitized = sanitized.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        }
        if (apiKey.Length > 0) sanitized = sanitized.Replace(apiKey, "[REDACTED]", StringComparison.Ordinal);
        sanitized = Regex.Replace(sanitized, @"\bBearer\s+\S+", "Bearer [REDACTED]", RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"\bsk-[A-Za-z0-9_-]+", "[REDACTED]", RegexOptions.IgnoreCase);
        return SanitizeField(sanitized);
    }

    private static string SanitizeField(string value)
    {
        string compact = Regex.Replace(value, @"[\r\n\t]+", " ").Trim();
        return compact.Length <= 500 ? compact : compact[..500] + "...";
    }

    private static void AddSensitiveValue(ISet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
    }

    private static void AddSensitiveJsonStrings(ISet<string> values, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            AddSensitiveJsonStrings(values, document.RootElement);
        }
        catch (JsonException)
        {
        }
    }

    private static void AddSensitiveJsonStrings(ISet<string> values, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AddSensitiveValue(values, element.GetString());
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray()) AddSensitiveJsonStrings(values, item);
                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject()) AddSensitiveJsonStrings(values, property.Value);
                break;
        }
    }
    private static string ReadString(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private sealed record ApiError(string Message, string Type, string Code);

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
        _lock.Dispose();
    }
}
