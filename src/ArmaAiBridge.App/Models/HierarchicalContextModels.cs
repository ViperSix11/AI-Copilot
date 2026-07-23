namespace ArmaAiBridge.App.Models;

public sealed record ContextCatalogueCategory(
    string Group,
    string Category,
    string Description,
    bool Available);

public sealed record ContextConversationTurn(
    string Role,
    string Text,
    DateTimeOffset CreatedAtUtc);

public sealed record ContextToolTrace(
    string Tool,
    string Group,
    string Category,
    string SafeArguments,
    string Result,
    int ResultUtf8Bytes,
    bool Truncated,
    long ElapsedMilliseconds);

public sealed record ContextUsageSample(
    DateTimeOffset CompletedAtUtc,
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens,
    int ToolCalls);

public sealed record ContextTraceSnapshot(
    string InteractionAlias,
    string Kind,
    string Seed,
    IReadOnlyList<string> AvailableGroups,
    IReadOnlyList<ContextToolTrace> ToolCalls,
    string ModelVisibleContext,
    string FinalDecision,
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens,
    int ToolCallCount,
    int RetrievalRounds,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<ContextUsageSample> RecentUsage);
