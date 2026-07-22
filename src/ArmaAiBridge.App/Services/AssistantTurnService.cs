using System.Diagnostics;
using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class AssistantTurnService : IAssistantTurnService, IDisposable
{
    public static readonly TimeSpan AcknowledgementDelay = TimeSpan.FromMilliseconds(5000);
    private readonly IOpenAiAssistantService _assistant;
    private readonly Func<string, (bool Success, string Snapshot)> _snapshotFactory;
    private readonly Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> _settingsFactory;
    private readonly Func<string, JsonElement, CancellationToken, Task<string>> _executeTool;
    private readonly Func<string> _currentGroupCallsignFactory;
    private readonly RadioAcknowledgementService _acknowledgements;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _acknowledgementDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _acknowledgementWait;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private bool _disposed;

    public AssistantTurnService(
        IOpenAiAssistantService assistant,
        Func<(bool Success, string Snapshot)> snapshotFactory,
        Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> settingsFactory,
        Func<string, JsonElement, CancellationToken, Task<string>> executeTool,
        Func<string>? currentGroupCallsignFactory = null,
        RadioAcknowledgementService? acknowledgements = null,
        TimeProvider? timeProvider = null,
        TimeSpan? acknowledgementDelay = null,
        Func<TimeSpan, CancellationToken, Task>? acknowledgementWait = null)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        _snapshotFactory = _ => snapshotFactory();
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _currentGroupCallsignFactory = currentGroupCallsignFactory ?? (() => string.Empty);
        _acknowledgements = acknowledgements ?? new RadioAcknowledgementService();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _acknowledgementDelay = acknowledgementDelay ?? AcknowledgementDelay;
        _acknowledgementWait = acknowledgementWait ?? ((duration, token) => Task.Delay(duration, _timeProvider, token));
    }

    public AssistantTurnService(
        IOpenAiAssistantService assistant,
        Func<string, (bool Success, string Snapshot)> snapshotFactory,
        Func<CancellationToken, Task<(string ApiKey, string Model, ResponseProfileSettings Profile)>> settingsFactory,
        Func<string, JsonElement, CancellationToken, Task<string>> executeTool,
        Func<string>? currentGroupCallsignFactory = null,
        RadioAcknowledgementService? acknowledgements = null,
        TimeProvider? timeProvider = null,
        TimeSpan? acknowledgementDelay = null,
        Func<TimeSpan, CancellationToken, Task>? acknowledgementWait = null)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _currentGroupCallsignFactory = currentGroupCallsignFactory ?? (() => string.Empty);
        _acknowledgements = acknowledgements ?? new RadioAcknowledgementService();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _acknowledgementDelay = acknowledgementDelay ?? AcknowledgementDelay;
        _acknowledgementWait = acknowledgementWait ?? ((duration, token) => Task.Delay(duration, _timeProvider, token));
    }

    public async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        CancellationToken cancellationToken)
        => await SubmitUserTurnAsync(text, source, (Func<RadioAcknowledgement, CancellationToken, Task>?)null, null, cancellationToken).ConfigureAwait(false);

    public async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        Action<RadioAcknowledgement>? acknowledgementReady,
        CancellationToken cancellationToken)
        => await SubmitUserTurnAsync(
            text,
            source,
            acknowledgementReady is null
                ? null
                : (acknowledgement, _) => { acknowledgementReady(acknowledgement); return Task.CompletedTask; },
            null,
            cancellationToken).ConfigureAwait(false);

    public async Task<AssistantResponse> SubmitUserTurnAsync(
        string text,
        UserTurnSource source,
        Func<RadioAcknowledgement, CancellationToken, Task>? acknowledgementReady,
        Action<AssistantResponse>? answerTextReady,
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
            (string apiKey, string model, ResponseProfileSettings profile) = await _settingsFactory(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch answerLatency = Stopwatch.StartNew();
            Task<AssistantResponse> responseTask = _assistant.AskAsync(
                apiKey,
                model,
                text.Trim(),
                snapshot,
                profile,
                _executeTool,
                cancellationToken);
            using CancellationTokenSource acknowledgementCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = _acknowledgementWait(_acknowledgementDelay, cancellationToken);
            Task acknowledgementDelivery = Task.CompletedTask;
            RadioAcknowledgement? acknowledgement = null;
            bool acknowledgementEmitted = false;
            if (await Task.WhenAny(responseTask, delay).ConfigureAwait(false) == delay && !responseTask.IsCompleted)
            {
                acknowledgement = _acknowledgements.Create(groupCallsign);
                if (acknowledgementReady is not null)
                {
                    acknowledgementEmitted = true;
                    acknowledgementDelivery = acknowledgementReady(acknowledgement, acknowledgementCancellation.Token);
                }
            }

            AssistantResponse response;
            try
            {
                response = await responseTask.ConfigureAwait(false);
            }
            catch
            {
                acknowledgementCancellation.Cancel();
                await ObserveAcknowledgementAsync(acknowledgementDelivery, cancellationToken).ConfigureAwait(false);
                throw;
            }
            answerLatency.Stop();
            AssistantRequestMetrics? metrics = response.RequestMetrics is null
                ? null
                : response.RequestMetrics with
                {
                    AcknowledgementVariationId = acknowledgementEmitted ? acknowledgement!.VariationId : string.Empty,
                    AcknowledgementEligible = true,
                    AcknowledgementEmitted = acknowledgementEmitted,
                    AcknowledgementThresholdMilliseconds = (int)_acknowledgementDelay.TotalMilliseconds,
                    AnswerTextLatencyMilliseconds = answerLatency.ElapsedMilliseconds
                };
            AssistantResponse final = response with
            {
                Text = RadioFinalResponsePolicy.EnsureCurrentCallsign(
                    response.Text,
                    groupCallsign),
                GroupCallsign = groupCallsign,
                RequestMetrics = metrics
            };
            answerTextReady?.Invoke(final);
            acknowledgementCancellation.Cancel();
            await ObserveAcknowledgementAsync(acknowledgementDelivery, cancellationToken).ConfigureAwait(false);
            return final;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private static async Task ObserveAcknowledgementAsync(Task delivery, CancellationToken turnToken)
    {
        try { await delivery.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested) { }
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
