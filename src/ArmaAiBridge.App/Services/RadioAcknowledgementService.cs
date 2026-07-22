using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ArmaAiBridge.App.Services;

public sealed record RadioAcknowledgement(
    string VisibleText,
    string SpokenText,
    string GroupCallsign,
    string VariationId);

public sealed class RadioAcknowledgementService
{
    private static readonly string[] EnglishTemplates =
    {
        "{0}, Papa Bear. Copy. Stand by.",
        "Papa Bear copies, {0}. Wait one.",
        "{0}, message received. Stand by.",
        "Copy that, {0}. Papa Bear is checking.",
        "Papa Bear has your request, {0}. Stand by.",
        "Roger, {0}. Checking now.",
        "{0}, Papa Bear copies all. Wait one.",
        "Copy, {0}. Stand by for an answer."
    };

    private readonly object _gate = new();
    private int _nextIndex;

    public RadioAcknowledgement Create(string? groupCallsign)
    {
        if (string.IsNullOrWhiteSpace(groupCallsign))
            return new RadioAcknowledgement(
                "Papa Bear copies. Stand by.",
                "Papa Bear copies. Stand by.",
                string.Empty,
                "ack-neutral");

        int index;
        lock (_gate)
        {
            index = _nextIndex;
            _nextIndex = (_nextIndex + 1) % EnglishTemplates.Length;
        }
        string visible = string.Format(CultureInfo.InvariantCulture, EnglishTemplates[index], groupCallsign);
        string spokenCallsign = CallsignSpeechFormatter.FormatCallsign(groupCallsign);
        string spoken = string.Format(CultureInfo.InvariantCulture, EnglishTemplates[index], spokenCallsign);
        return new RadioAcknowledgement(visible, spoken, groupCallsign, $"ack-en-{index + 1:00}");
    }

    public static IReadOnlyList<string> Templates => EnglishTemplates;
}

public static class CallsignSpeechFormatter
{
    private static readonly string[] Digits =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine"
    };

    public static string FormatCallsign(string callsign)
    {
        string normalized = Regex.Replace(callsign ?? string.Empty, @"\s+", " ").Trim();
        StringBuilder result = new(normalized.Length * 2);
        foreach (char character in normalized)
        {
            if (char.IsAsciiDigit(character)) result.Append(Digits[character - '0']);
            else if (character is '/' or '\\' or '_') result.Append(", ");
            else result.Append(character);
        }
        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }

    public static string FormatAnswerForSpeech(string visibleAnswer, string? currentGroupCallsign)
    {
        if (string.IsNullOrWhiteSpace(currentGroupCallsign)) return visibleAnswer;
        return visibleAnswer.Replace(
            currentGroupCallsign,
            FormatCallsign(currentGroupCallsign),
            StringComparison.Ordinal);
    }
}

public static class RadioFinalResponsePolicy
{
    public static string EnsureCurrentCallsign(string visibleAnswer, string? currentGroupCallsign)
    {
        if (string.IsNullOrWhiteSpace(currentGroupCallsign) ||
            visibleAnswer.Contains(currentGroupCallsign, StringComparison.Ordinal))
        {
            return visibleAnswer;
        }

        return $"{currentGroupCallsign}, {visibleAnswer.TrimStart()}";
    }
}
