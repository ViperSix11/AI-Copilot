using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class ContextConversationStore
{
    private const int MaximumTurns = 40;
    private readonly object _gate = new();
    private readonly List<ContextConversationTurn> _turns = new();
    private readonly TimeProvider _timeProvider;

    public ContextConversationStore(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public void Add(string role, string text)
    {
        string normalizedRole = role is "user" or "assistant" ? role : throw new InvalidOperationException("Unsupported conversation role.");
        string normalizedText = (text ?? string.Empty).Trim();
        if (normalizedText.Length == 0) return;
        if (normalizedText.Length > 4000) normalizedText = normalizedText[..4000];
        lock (_gate)
        {
            _turns.Add(new ContextConversationTurn(normalizedRole, normalizedText, _timeProvider.GetUtcNow()));
            if (_turns.Count > MaximumTurns) _turns.RemoveRange(0, _turns.Count - MaximumTurns);
        }
    }

    public IReadOnlyList<ContextConversationTurn> GetRecent(int limit)
    {
        lock (_gate)
            return _turns.TakeLast(Math.Clamp(limit, 1, MaximumTurns)).ToArray();
    }

    public void Clear()
    {
        lock (_gate) _turns.Clear();
    }
}
