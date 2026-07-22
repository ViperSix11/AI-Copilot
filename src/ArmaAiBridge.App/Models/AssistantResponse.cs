namespace ArmaAiBridge.App.Models;

public sealed record AssistantResponse(
    string Text,
    string Model,
    int ToolCalls,
    int InputTokens,
    int OutputTokens,
    int ReasoningTokens = 0);
