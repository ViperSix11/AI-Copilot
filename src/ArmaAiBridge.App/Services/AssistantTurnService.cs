using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class AssistantTurnService : IAssistantTurnService, IDisposable
{
    private readonly IOpenAiAssistantService _assistant;
    private readonly Func<(bool Success, string Snapshot)> _snapshotFactory;
    private readonly Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> _settingsFactory;
    private readonly Func<string, JsonElement, CancellationToken, Task<string>> _executeTool;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private bool _disposed;

    public AssistantTurnService(
        IOpenAiAssistantService assistant,
        Func<(bool Success, string Snapshot)> snapshotFactory,
        Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> settingsFactory,
        Func<string, JsonElement, CancellationToken, Task<string>> executeTool)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
    }

    public async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Enter a question first.");
        if (!await _turnGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An assistant turn is already active.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (bool success, string snapshot) = _snapshotFactory();
            if (!success)
                throw new InvalidOperationException("No local Arma world state is available yet.");

            (string apiKey, string model, ResponseProfileSettings profile) = await _settingsFactory(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await _assistant.AskAsync(
                apiKey,
                model,
                text.Trim(),
                snapshot,
                profile,
                _executeTool,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void ResetConversation() => _assistant.ResetConversation();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _assistant.Dispose();
        _turnGate.Dispose();
    }
}
