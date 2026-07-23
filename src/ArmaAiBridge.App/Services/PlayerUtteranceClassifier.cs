namespace ArmaAiBridge.App.Services;

public static class PlayerUtteranceClassifier
{
    private static readonly string[] QuestionPrefixes =
    {
        "what ", "where ", "when ", "why ", "how ", "who ", "which ",
        "can you ", "could you ", "would you ", "tell me ", "give me ", "show me ", "please ",
        "any ", "anything ", "anyone ", "anybody ", "do we ", "did we ", "are there ", "is there ",
        "have we ", "has there ", "seen any ", "got any ", "do you have ", "does the ", "is the ", "are the "
    };
    private static readonly string[] IntentPrefixes =
    {
        "i want to ", "we want to ", "i want us to ", "i would like to ", "i'd like to ",
        "i plan to ", "we plan to ", "i intend to ", "we intend to ", "our plan is ", "the plan is to ",
        "i am going to ", "i'm going to ", "we are going to ", "we're going to ",
        "i need to ", "we need to ", "i should ", "we should ", "let's ", "let us ",
        "attack ", "assault ", "storm ", "capture ", "take the ", "move to ", "go to ", "engage "
    };
    private static readonly string[] TentativePhrases =
    {
        "it seems ", "seems like ", "i think ", "i believe ", "appears to be ", "it appears ",
        "looks like ", "sounds like ", "i may have seen ", "i might have seen "
    };

    public static bool IsQuestion(string value)
    {
        string text = Normalize(value);
        return text.EndsWith('?') || QuestionPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static bool IsPlanningIntent(string value)
    {
        string text = Normalize(value);
        return IntentPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static bool IsTentativeReport(string value)
    {
        string text = Normalize(value);
        return TentativePhrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
    }

    public static bool IsAffirmative(string value)
    {
        string text = Normalize(value).TrimEnd('.', '!');
        return text is "yes" or "affirmative" or "confirm" or "confirmed" or "i confirm" or "that is confirmed" or "correct";
    }

    public static bool IsNegativeOrCancel(string value)
    {
        string text = Normalize(value).TrimEnd('.', '!');
        return text is "no" or "negative" or "cancel" or "cancel report" or "disregard" or "not confirmed" or "i cannot confirm";
    }

    private static string Normalize(string? value)
        => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
