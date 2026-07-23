using System.Collections.Concurrent;
using System.Text.Json;

namespace ArmaAiBridge.App.Services;

public sealed class ArmaQueryCoordinator : IDisposable
{
    private const string ResultSchema = "arma-ai-bridge/arma3/query-result-v1";
    private const string CommandSchema = "arma-ai-bridge/command-v1";
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    { "building", "vegetation", "road", "wall", "rock" };

    private readonly TelemetryPipeServer _pipe;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();
    private bool _disposed;

    public ArmaQueryCoordinator(TelemetryPipeServer pipe)
    {
        _pipe = pipe;
        _pipe.MessageReceived += OnMessage;
    }

    public async Task<string> QueryEnvironmentAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string shape = ReadEnum(arguments, "shape", "cone", "circle");
        string direction = ReadEnum(arguments, "direction", "view", "body");
        double range = ReadNumber(arguments, "rangeMeters", 25, 1500);
        double angle = ReadNumber(arguments, "angleDegrees", 5, 180);
        int limit = (int)ReadNumber(arguments, "maxResultsPerCategory", 1, 50, integer: true);
        string[] categories = ReadCategories(arguments);

        Dictionary<string, object> parameters = new()
        {
            ["origin"] = "player",
            ["shape"] = shape,
            ["direction"] = direction,
            [shape == "circle" ? "radiusMeters" : "rangeMeters"] = range,
            ["categories"] = categories,
            ["maxResultsPerCategory"] = limit
        };
        if (shape == "cone") parameters["angleDegrees"] = angle;

        return await SendQueryAsync("query_environment", parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendQueryAsync(string commandName, object parameters, CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string command = JsonSerializer.Serialize(new
        {
            schema = CommandSchema,
            requestId,
            command = commandName,
            parameters
        });

        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Could not register Arma query.");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using CancellationTokenRegistration registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));

        try
        {
            if (!await _pipe.SendCommandAsync(command, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Arma is not connected.");
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
                throw new TimeoutException("Arma did not return the local read query within 12 seconds.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private void OnMessage(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string schema = ReadString(root, "schema");
            if (schema != ResultSchema) return;
            string requestId = ReadString(root, "requestId");
            if (_pending.TryGetValue(requestId, out TaskCompletionSource<string>? completion))
                completion.TrySetResult(json);
        }
        catch (JsonException) { }
    }

    private static string ReadEnum(JsonElement root, string name, params string[] allowed)
    {
        string value = ReadString(root, name).ToLowerInvariant();
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new InvalidOperationException($"Unsupported {name}: {value}.");
        return value;
    }

    private static double ReadNumber(JsonElement root, string name, double min, double max, bool integer = false)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) || number < min || number > max || (integer && number != Math.Truncate(number)))
            throw new InvalidOperationException($"{name} must be between {min} and {max}.");
        return number;
    }

    private static string[] ReadCategories(JsonElement root)
    {
        if (!root.TryGetProperty("categories", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("At least one terrain category is required.");
        string[] categories = array.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.ToLowerInvariant() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (categories.Length is < 1 or > 5 || categories.Any(x => !AllowedCategories.Contains(x)))
            throw new InvalidOperationException("The terrain categories are invalid.");
        return categories;
    }

    private static string ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.MessageReceived -= OnMessage;
        foreach (TaskCompletionSource<string> item in _pending.Values) item.TrySetCanceled();
        _pending.Clear();
    }
}
