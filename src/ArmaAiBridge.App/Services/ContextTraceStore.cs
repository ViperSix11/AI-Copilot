using System.Text;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class ContextTraceStore
{
    private readonly object _gate = new();
    private readonly List<ContextToolTrace> _tools = new();
    private readonly List<ContextUsageSample> _usage = new();
    private string _interactionAlias = string.Empty;
    private string _kind = string.Empty;
    private string _seed = string.Empty;
    private string _modelContext = string.Empty;
    private string _finalDecision = string.Empty;
    private int _inputTokens;
    private int _outputTokens;
    private int _reasoningTokens;
    private int _retrievalRounds;
    private DateTimeOffset? _completedAtUtc;

    public event Action? Changed;

    public void Begin(string interactionAlias, string kind, string seed)
    {
        lock (_gate)
        {
            _interactionAlias = interactionAlias;
            _kind = kind;
            _seed = Pretty(seed);
            _tools.Clear();
            _modelContext = string.Empty;
            _finalDecision = string.Empty;
            _inputTokens = _outputTokens = _reasoningTokens = _retrievalRounds = 0;
            _completedAtUtc = null;
        }
        Changed?.Invoke();
    }

    public void AddTool(string name, string arguments, string result, long elapsedMilliseconds)
    {
        string group = string.Empty;
        string category = string.Empty;
        bool truncated = false;
        try
        {
            using JsonDocument args = JsonDocument.Parse(arguments);
            group = ReadString(args.RootElement, "group");
            category = ReadString(args.RootElement, "category");
            using JsonDocument output = JsonDocument.Parse(result);
            truncated = output.RootElement.TryGetProperty("truncated", out JsonElement value) &&
                        value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { }
        ContextToolTrace trace = new(
            name,
            group,
            category,
            Pretty(arguments),
            Pretty(result),
            Encoding.UTF8.GetByteCount(result),
            truncated,
            elapsedMilliseconds);
        lock (_gate)
        {
            _tools.Add(trace);
            _retrievalRounds++;
            _modelContext = string.Join(
                Environment.NewLine + Environment.NewLine,
                _tools.Select(item => $"{item.Tool}\n{item.Result}"));
        }
        Changed?.Invoke();
    }

    public void Complete(string decision, int inputTokens, int outputTokens, int reasoningTokens)
    {
        DateTimeOffset completed = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _finalDecision = decision;
            _inputTokens = Math.Max(0, inputTokens);
            _outputTokens = Math.Max(0, outputTokens);
            _reasoningTokens = Math.Max(0, reasoningTokens);
            _completedAtUtc = completed;
            _usage.Add(new ContextUsageSample(
                completed,
                _inputTokens,
                _outputTokens,
                _reasoningTokens,
                _tools.Count));
            _usage.RemoveAll(item => completed - item.CompletedAtUtc > TimeSpan.FromMinutes(30));
        }
        Changed?.Invoke();
    }

    public ContextTraceSnapshot GetSnapshot()
    {
        lock (_gate)
            return new ContextTraceSnapshot(
                _interactionAlias,
                _kind,
                _seed,
                HierarchicalContextCatalogue.Groups,
                _tools.ToArray(),
                _modelContext,
                _finalDecision,
                _inputTokens,
                _outputTokens,
                _reasoningTokens,
                _tools.Count,
                _retrievalRounds,
                _completedAtUtc,
                _usage.ToArray());
    }

    public void Reset()
    {
        lock (_gate)
        {
            _interactionAlias = _kind = _seed = _modelContext = _finalDecision = string.Empty;
            _tools.Clear();
            _inputTokens = _outputTokens = _reasoningTokens = _retrievalRounds = 0;
            _completedAtUtc = null;
        }
        Changed?.Invoke();
    }

    private static string Pretty(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json; }
    }

    private static string ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
