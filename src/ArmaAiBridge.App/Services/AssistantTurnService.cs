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
    private Func<string, (bool Handled, string Response)>? _localTurnHandler;
    private readonly RadioAcknowledgementService _acknowledgements;
    private readonly NaturalRadioDialogueService _radioDialogue;
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
        Func<TimeSpan, CancellationToken, Task>? acknowledgementWait = null,
        NaturalRadioDialogueService? radioDialogue = null)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        ArgumentNullException.ThrowIfNull(snapshotFactory);
        _snapshotFactory = _ => snapshotFactory();
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _currentGroupCallsignFactory = currentGroupCallsignFactory ?? (() => string.Empty);
        _acknowledgements = acknowledgements ?? new RadioAcknowledgementService();
        _radioDialogue = radioDialogue ?? new NaturalRadioDialogueService(timeProvider: timeProvider);
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
        Func<TimeSpan, CancellationToken, Task>? acknowledgementWait = null,
        NaturalRadioDialogueService? radioDialogue = null)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _settingsFactory = settingsFactory ?? throw new ArgumentNullException(nameof(settingsFactory));
        _executeTool = executeTool ?? throw new ArgumentNullException(nameof(executeTool));
        _currentGroupCallsignFactory = currentGroupCallsignFactory ?? (() => string.Empty);
        _acknowledgements = acknowledgements ?? new RadioAcknowledgementService();
        _radioDialogue = radioDialogue ?? new NaturalRadioDialogueService(timeProvider: timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _acknowledgementDelay = acknowledgementDelay ?? AcknowledgementDelay;
        _acknowledgementWait = acknowledgementWait ?? ((duration, token) => Task.Delay(duration, _timeProvider, token));
    }

    public void SetLocalTurnHandler(Func<string, (bool Handled, string Response)> handler)
        => _localTurnHandler = handler ?? throw new ArgumentNullException(nameof(handler));

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
            string normalizedText = text.Trim();
            string immediateCallsign = _currentGroupCallsignFactory().Trim();
            if (_radioDialogue.TryHandleFollowUp(normalizedText, immediateCallsign, out NaturalRadioPlan followUp))
            {
                AssistantResponse localFollowUp = CreateLocalResponse(
                    followUp.Transmissions.Count == 0
                        ? string.Empty
                        : string.Join(' ', followUp.Transmissions.Select(item => item.Text)),
                    immediateCallsign,
                    followUp);
                answerTextReady?.Invoke(localFollowUp);
                return localFollowUp;
            }
            if (_localTurnHandler?.Invoke(normalizedText) is (true, string localText))
            {
                string callsign = immediateCallsign;
                string visible = RadioFinalResponsePolicy.EnsureCurrentCallsign(localText, callsign);
                NaturalRadioPlan localPlan = _radioDialogue.Plan(
                    normalizedText,
                    visible,
                    callsign,
                    acknowledgementAlreadyEmitted: false);
                AssistantResponse local = CreateLocalResponse(visible, callsign, localPlan);
                answerTextReady?.Invoke(local);
                return local;
            }
            if (FiringSolutionRequestPolicy.IsRequest(normalizedText))
            {
                string callsign = immediateCallsign;
                string message = callsign.Length == 0
                    ? "Firing-solution calculation is not available."
                    : $"{callsign}, firing-solution calculation is not available.";
                NaturalRadioPlan localPlan = new(
                    new[] { new RadioTransmission(message) },
                    false,
                    "radio-local-single");
                AssistantResponse local = CreateLocalResponse(message, callsign, localPlan);
                answerTextReady?.Invoke(local);
                return local;
            }
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
            string finalText = RadioFinalResponsePolicy.EnsureCurrentCallsign(
                response.Text,
                groupCallsign);
            NaturalRadioPlan radioPlan = _radioDialogue.Plan(
                normalizedText,
                finalText,
                groupCallsign,
                acknowledgementEmitted,
                profile);
            metrics = metrics is null
                ? null
                : metrics with
                {
                    RadioVariationId = radioPlan.VariationId,
                    RadioTransmissionCount = radioPlan.Transmissions.Count,
                    CopyConfirmationRequested = radioPlan.AwaitingCopyConfirmation
                };
            AssistantResponse final = response with
            {
                Text = finalText,
                GroupCallsign = groupCallsign,
                RequestMetrics = metrics,
                Transmissions = radioPlan.Transmissions,
                AwaitingCopyConfirmation = radioPlan.AwaitingCopyConfirmation
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

    public void ResetConversation()
    {
        _assistant.ResetConversation();
        _radioDialogue.Reset();
    }

    private static AssistantResponse CreateLocalResponse(
        string text,
        string callsign,
        NaturalRadioPlan plan)
        => new(
            text,
            "local",
            0,
            0,
            0,
            GroupCallsign: callsign,
            RequestMetrics: new AssistantRequestMetrics(
                0,
                new Dictionary<string, int>(),
                0,
                0,
                0,
                0,
                RadioVariationId: plan.VariationId,
                RadioTransmissionCount: plan.Transmissions.Count,
                CopyConfirmationRequested: plan.AwaitingCopyConfirmation),
            Transmissions: plan.Transmissions,
            AwaitingCopyConfirmation: plan.AwaitingCopyConfirmation);

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

public static class FiringSolutionRequestPolicy
{
    private static readonly string[] Terms =
    {
        "firing solution", "fire solution", "elevation correction", "wind hold", "scope clicks",
        "time of flight", "ballistic solution"
    };

    public static bool IsRequest(string text)
    {
        string value = (text ?? string.Empty).Trim().ToLowerInvariant();
        return Terms.Any(value.Contains);
    }
}
