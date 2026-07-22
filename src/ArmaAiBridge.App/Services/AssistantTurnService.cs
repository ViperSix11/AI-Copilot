using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class AssistantTurnService : IAssistantTurnService, IDisposable
{
    private readonly IOpenAiAssistantService _assistant;
    private readonly Func<string, (bool Success, string Snapshot)> _snapshotFactory;
    private readonly Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> _settingsFactory;
    private readonly Func<string, JsonElement, CancellationToken, Task<string>> _executeTool;
    private readonly Func<string> _currentGroupCallsignFactory;
    private readonly RadioAcknowledgementService _acknowledgements;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private bool _disposed;

    public AssistantTurnService(
        IOpenAiAssistantService assistant,
        Func<(bool Success, string Snapshot)> snapshotFactory,
        Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> settingsFactory,
        Func<string, JsonElement, CancellationToken, Task<string>> executeTool,
        Func<string>? currentGroupCallsignFactory = null,
        RadioAcknowledgementService? acknowledgements = null)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        _snapshotFactory = _ => snapshotFactory();
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _currentGroupCallsignFactory = currentGroupCallsignFactory ?? (() => string.Empty);
        _acknowledgements = acknowledgements ?? new RadioAcknowledgementService();
    }

    public AssistantTurnService(
        IOpenAiAssistantService assistant,
        Func<string, (bool Success, string Snapshot)> snapshotFactory,
        Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> settingsFactory,
        Func<string, JsonElement, CancellationToken, Task<string>> executeTool,
        Func<string>? currentGroupCallsignFactory = null,
        RadioAcknowledgementService? acknowledgements = null)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _currentGroupCallsignFactory = currentGroupCallsignFactory ?? (() => string.Empty);
        _acknowledgements = acknowledgements ?? new RadioAcknowledgementService();
    }

    public async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        CancellationToken cancellationToken)
        => await SubmitUserTurnAsync(text, source, null, cancellationToken).ConfigureAwait(false);

    public async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        Action<RadioAcknowledgement>? acknowledgementReady,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Enter a question first.");
        if (!await _turnGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An assistant turn is already active.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (bool success, string snapshot) = _snapshotFactory(text.Trim());
            if (!success)
                throw new InvalidOperationException("No local Arma world state is available yet.");

            string groupCallsign = ReadSnapshotCallsign(snapshot);
            if (groupCallsign.Length == 0) groupCallsign = _currentGroupCallsignFactory();
            RadioAcknowledgement acknowledgement = _acknowledgements.Create(groupCallsign);
            acknowledgementReady?.Invoke(acknowledgement);

            (string apiKey, string model, ResponseProfileSettings profile) = await _settingsFactory(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AssistantResponse response = await _assistant.AskAsync(
                apiKey,
                model,
                text.Trim(),
                snapshot,
                profile,
                _executeTool,
                cancellationToken).ConfigureAwait(false);
            AssistantRequestMetrics? metrics = response.RequestMetrics is null
                ? null
                : response.RequestMetrics with { AcknowledgementVariationId = acknowledgement.VariationId };
            return response with
            {
                Text = RadioFinalResponsePolicy.EnsureCurrentCallsign(
                    response.Text,
                    acknowledgement.GroupCallsign),
                GroupCallsign = acknowledgement.GroupCallsign,
                RequestMetrics = metrics
            };
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void ResetConversation() => _assistant.ResetConversation();

    private static string ReadSnapshotCallsign(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(snapshot);
            return document.RootElement.TryGetProperty("player", out JsonElement player) &&
                   player.TryGetProperty("groupCallsign", out JsonElement callsign) &&
                   callsign.ValueKind == JsonValueKind.String
                ? callsign.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _assistant.Dispose();
        _turnGate.Dispose();
    }
}
