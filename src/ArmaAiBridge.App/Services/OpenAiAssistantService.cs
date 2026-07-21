using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class OpenAiAssistantService : IDisposable
{
    private const string Instructions = """
You are ArmA AI Bridge, a concise tactical assistant for the local player in Arma 3.
Answer in the user's language, use metric units, and distinguish measured facts from tactical interpretation.
Treat the supplied telemetry as the only current game state. Never invent map objects, contacts, positions, visibility, routes, or threats.
Use query_environment whenever a question depends on actual terrain objects. Use a circle for the general vicinity and a cone for ahead/in-view questions.
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
        }
    };

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri("https://api.openai.com/v1/"),
        Timeout = TimeSpan.FromSeconds(60)
    };
    private readonly List<(string Role, string Text)> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<AssistantResponse> AskAsync(
        string apiKey, string model, string question, string telemetryJson,
        Func<JsonElement, CancellationToken, Task<string>> queryEnvironment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Save an OpenAI API key first.");
        if (string.IsNullOrWhiteSpace(question)) throw new InvalidOperationException("Enter a question first.");
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<object> input = _history.Select(x => (object)Message(x.Role, x.Text)).ToList();
            input.Add(Message("user", $"CURRENT PRIVACY-MINIMIZED ARMA TELEMETRY:\n{MinimizeTelemetry(telemetryJson)}\n\nQUESTION:\n{question.Trim()}"));
            int toolCalls = 0, inputTokens = 0, outputTokens = 0;
            string effectiveModel = string.IsNullOrWhiteSpace(model) ? "gpt-5-mini" : model.Trim();

            for (int round = 0; round < 4; round++)
            {
                using JsonDocument response = await PostAsync(apiKey.Trim(), effectiveModel, input, cancellationToken).ConfigureAwait(false);
                JsonElement root = response.RootElement;
                effectiveModel = ReadString(root, "model", effectiveModel);
                AddUsage(root, ref inputTokens, ref outputTokens);
                if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("OpenAI returned no output array.");

                List<JsonElement> items = output.EnumerateArray().Select(x => x.Clone()).ToList();
                List<JsonElement> calls = items.Where(x => ReadString(x, "type") == "function_call").ToList();
                if (calls.Count == 0)
                {
                    string answer = ExtractText(items);
                    if (answer.Length == 0) throw new InvalidOperationException("OpenAI returned no answer.");
                    AddHistory(question.Trim(), answer);
                    return new AssistantResponse(answer, effectiveModel, toolCalls, inputTokens, outputTokens);
                }
                if (round == 3) throw new InvalidOperationException("OpenAI exceeded three local tool rounds.");
                foreach (JsonElement item in items) input.Add(item);
                foreach (JsonElement call in calls)
                {
                    toolCalls++;
                    string callId = ReadString(call, "call_id");
                    string name = ReadString(call, "name");
                    string result;
                    try
                    {
                        if (name != "query_environment") throw new InvalidOperationException($"Unsupported tool: {name}.");
                        using JsonDocument args = JsonDocument.Parse(ReadString(call, "arguments", "{}"));
                        result = await queryEnvironment(args.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
                    }
                    input.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = result
                    });
                }
            }
            throw new InvalidOperationException("Assistant tool loop failed.");
        }
        finally { _lock.Release(); }
    }

    public void ResetConversation() => _history.Clear();

    private async Task<JsonDocument> PostAsync(string apiKey, string model, IReadOnlyList<object> input, CancellationToken token)
    {
        Dictionary<string, object?> body = new()
        {
            ["model"] = model, ["instructions"] = Instructions, ["input"] = input, ["tools"] = Tools,
            ["tool_choice"] = "auto", ["parallel_tool_calls"] = false, ["max_output_tokens"] = 600, ["store"] = false
        };
        using HttpRequestMessage request = new(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("ArmA-AI-Bridge/0.3.0");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"OpenAI API error {(int)response.StatusCode}: {ReadApiError(json)}");
        return JsonDocument.Parse(json);
    }

    private static string MinimizeTelemetry(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Dictionary<string, object?> data = new()
        {
            ["map"] = PickObject(root, "map", "name", "sizeMeters", "grid", "daytime"),
            ["player"] = PickObject(root, "player", "side", "positionATL", "bodyHeading", "viewHeading", "speedKph", "damage", "lifeState", "stance", "weapon", "loadedRounds"),
            ["vehicle"] = PickNullable(root, "vehicle", "class", "displayName", "heading", "speedKph", "fuel", "damage", "role"),
            ["contacts"] = PickArray(root, "contacts", "class", "displayName", "knownByPlayer", "knownByGroup", "lastSeenAgeSeconds", "perceivedSide", "positionErrorMeters", "estimatedPosition"),
            ["sensorContacts"] = PickArray(root, "sensorContacts", "class", "targetType", "relationship", "sensors")
        };
        return JsonSerializer.Serialize(data);
    }

    private static Dictionary<string, object?> PickObject(JsonElement parent, string name, params string[] fields)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? Pick(value, fields) : new();
    private static object? PickNullable(JsonElement parent, string name, params string[] fields)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? Pick(value, fields) : null;
    private static List<Dictionary<string, object?>> PickArray(JsonElement parent, string name, params string[] fields)
        => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(32).Select(x => Pick(x, fields)).ToList() : new();
    private static Dictionary<string, object?> Pick(JsonElement value, IEnumerable<string> fields)
    {
        Dictionary<string, object?> result = new();
        foreach (string field in fields) if (value.TryGetProperty(field, out JsonElement item)) result[field] = item.Clone();
        return result;
    }

    private void AddHistory(string question, string answer)
    {
        _history.Add(("user", Truncate(question))); _history.Add(("assistant", Truncate(answer)));
        while (_history.Count > 12) _history.RemoveAt(0);
    }
    private static string Truncate(string value) => value.Length <= 4000 ? value : value[..4000];
    private static Dictionary<string, object?> Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
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
    private static string ReadApiError(string json)
    {
        try { using JsonDocument doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("error", out JsonElement e) ? ReadString(e, "message", "Request rejected.") : "Request rejected."; }
        catch (JsonException) { return "Request rejected."; }
    }
    private static string ReadString(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static int ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    public void Dispose() { _http.Dispose(); _lock.Dispose(); }
}
