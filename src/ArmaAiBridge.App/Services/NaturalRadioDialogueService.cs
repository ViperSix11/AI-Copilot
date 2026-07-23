using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed record NaturalRadioPlan(
    IReadOnlyList<RadioTransmission> Transmissions,
    bool AwaitingCopyConfirmation,
    string VariationId);

public sealed partial class NaturalRadioDialogueService
{
    private static readonly TimeSpan FollowUpLifetime = TimeSpan.FromMinutes(5);

    private static readonly string[] PreparationWithCallsign =
    {
        "{0}, stand by for new information.",
        "{0}, I have new information. Stand by.",
        "Stand by, {0}. Information follows.",
        "{0}, give me a second. I have an update."
    };

    private static readonly string[] PreparationWithoutCallsign =
    {
        "Stand by for new information.",
        "I have new information. Stand by.",
        "Information follows.",
        "Give me a second. I have an update."
    };

    private static readonly string[] CopyRequests =
    {
        "Do you copy?",
        "Confirm you received that.",
        "Copy?"
    };

    private readonly Func<int, int> _next;
    private readonly TimeProvider _timeProvider;
    private LastTransmission? _last;
    private string _lastCallsign = string.Empty;

    public NaturalRadioDialogueService(
        Func<int, int>? next = null,
        TimeProvider? timeProvider = null)
    {
        _next = next ?? Random.Shared.Next;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public NaturalRadioPlan Plan(
        string question,
        string answer,
        string? currentCallsign,
        bool acknowledgementAlreadyEmitted,
        ResponseProfileSettings? responseProfile = null)
    {
        string callsign = NormalizeCallsign(currentCallsign);
        ClearForCallsignChange(callsign);
        string content = NormalizeText(answer);
        if (content.Length == 0)
            return new NaturalRadioPlan(Array.Empty<RadioTransmission>(), false, "radio-empty");

        string[] parts = SplitContent(content);
        bool urgent = UrgentRegex().IsMatch($"{question} {content}");
        bool operational = OperationalRegex().IsMatch($"{question} {content}");
        bool complex = content.Length >= 180 || parts.Length >= 3 || ComplexityRegex().Matches(content).Count >= 2;
        int callsignChance = urgent ? 75 : complex ? 45 : 30;
        bool addressPlayer = callsign.Length > 0 && Roll(callsignChance);
        content = addressPlayer
            ? RadioFinalResponsePolicy.EnsureCurrentCallsign(content, callsign)
            : RemoveLeadingCallsign(content, callsign);
        parts = SplitContent(content);
        int splitChance = parts.Length < 2 || content.Length < 120
            ? 0
            : urgent ? 45
            : complex ? 60
            : 25;
        bool split = Roll(splitChance);

        List<string> contentCalls = split ? Balance(parts) : new List<string> { content };
        bool preparation = split &&
                           !acknowledgementAlreadyEmitted &&
                           Roll(urgent ? 10 : 35);

        ResponseProfileSettings profile = ResponseProfilePolicy.Normalize(responseProfile);
        bool answerAlreadyAsksQuestion = content.TrimEnd().EndsWith("?", StringComparison.Ordinal);
        int confirmationChance = !operational || answerAlreadyAsksQuestion ||
                                 profile.Terminator is "out" or "custom"
            ? 0
            : urgent ? 40
            : complex ? 30
            : 12;
        bool requestCopy = Roll(confirmationChance);

        List<RadioTransmission> transmissions = new();
        string variation = $"radio-{(urgent ? "urgent" : "calm")}-{(split ? "split" : "single")}" +
                           (addressPlayer ? "-address" : string.Empty);
        if (preparation)
        {
            bool useCallsign = callsign.Length > 0 && Roll(70);
            string[] templates = useCallsign ? PreparationWithCallsign : PreparationWithoutCallsign;
            string template = Pick(templates);
            string preparationText = useCallsign
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, template, callsign)
                : template;
            transmissions.Add(new RadioTransmission(preparationText));
            if (useCallsign && contentCalls.Count > 0)
                contentCalls[0] = RemoveLeadingCallsign(contentCalls[0], callsign);
            variation += "-prepare";
        }

        int pause = urgent ? 250 + NextBounded(151) : 500 + NextBounded(351);
        for (int index = 0; index < contentCalls.Count; index++)
        {
            string call = contentCalls[index].Trim();
            if (call.Length == 0) continue;
            transmissions.Add(new RadioTransmission(
                call,
                transmissions.Count == 0 ? 0 : pause));
        }

        if (requestCopy && transmissions.Count > 0)
        {
            RadioTransmission final = transmissions[^1];
            string finalContent = profile.Terminator == "over"
                ? TrailingOverRegex().Replace(final.Text, string.Empty).TrimEnd()
                : final.Text.TrimEnd();
            string terminator = profile.Terminator == "over" ? " Over." : string.Empty;
            transmissions[^1] = final with { Text = $"{finalContent} {Pick(CopyRequests)}{terminator}" };
            variation += "-copy";
        }

        _last = new LastTransmission(
            content,
            requestCopy,
            _timeProvider.GetUtcNow());
        _lastCallsign = callsign;
        return new NaturalRadioPlan(transmissions, requestCopy, variation);
    }

    public bool TryHandleFollowUp(
        string input,
        string? currentCallsign,
        out NaturalRadioPlan plan)
    {
        string callsign = NormalizeCallsign(currentCallsign);
        ClearForCallsignChange(callsign);
        plan = new NaturalRadioPlan(Array.Empty<RadioTransmission>(), false, string.Empty);
        if (_last is null ||
            _timeProvider.GetUtcNow() - _last.CreatedAtUtc > FollowUpLifetime)
        {
            _last = null;
            return false;
        }

        string normalized = NormalizeInput(input);
        if (RepeatRegex().IsMatch(normalized))
        {
            string repeated = SimplifyForRepeat(_last.Content, callsign);
            bool awaitCopy = _last.AwaitingCopy;
            if (awaitCopy) repeated = $"{repeated.TrimEnd()} Do you copy?";
            _last = _last with { CreatedAtUtc = _timeProvider.GetUtcNow() };
            plan = new NaturalRadioPlan(
                new[] { new RadioTransmission(repeated) },
                awaitCopy,
                "radio-followup-repeat");
            return true;
        }

        if (_last.AwaitingCopy && AffirmativeRegex().IsMatch(normalized))
        {
            _last = _last with { AwaitingCopy = false, CreatedAtUtc = _timeProvider.GetUtcNow() };
            plan = new NaturalRadioPlan(
                new[] { new RadioTransmission("Roger.") },
                false,
                "radio-followup-copy");
            return true;
        }

        if (_last.AwaitingCopy && NegativeRegex().IsMatch(normalized))
        {
            _last = _last with { AwaitingCopy = false, CreatedAtUtc = _timeProvider.GetUtcNow() };
            plan = new NaturalRadioPlan(
                new[] { new RadioTransmission("Understood. Say again what you need clarified.") },
                false,
                "radio-followup-negative");
            return true;
        }

        if (_last.AwaitingCopy)
        {
            plan = new NaturalRadioPlan(
                new[]
                {
                    new RadioTransmission(
                        "Confirm receipt, say negative, or ask me to repeat the last information.")
                },
                true,
                "radio-followup-awaiting-copy");
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _last = null;
        _lastCallsign = string.Empty;
    }

    private void ClearForCallsignChange(string callsign)
    {
        if (_last is not null &&
            !string.Equals(_lastCallsign, callsign, StringComparison.Ordinal))
        {
            _last = null;
        }
        _lastCallsign = callsign;
    }

    private bool Roll(int percent)
        => percent > 0 && NextBounded(100) < percent;

    private int NextBounded(int maximum)
    {
        if (maximum <= 1) return 0;
        int value = _next(maximum);
        if (value == int.MinValue) value = 0;
        value = Math.Abs(value);
        return value % maximum;
    }

    private string Pick(IReadOnlyList<string> values)
        => values[NextBounded(values.Count)];

    private static string[] SplitContent(string content)
    {
        List<string> sentences = SentenceBoundaryRegex()
            .Split(content)
            .Select(NormalizeText)
            .Where(value => value.Length > 0)
            .ToList();
        if (sentences.Count > 1 && RadioTerminatorRegex().IsMatch(sentences[^1]))
        {
            sentences[^2] = $"{sentences[^2]} {sentences[^1]}";
            sentences.RemoveAt(sentences.Count - 1);
        }
        if (sentences.Count > 1) return sentences.ToArray();

        if (content.Length >= 180)
        {
            string[] clauses = ClauseBoundaryRegex()
                .Split(content)
                .Select(NormalizeText)
                .Where(value => value.Length > 0)
                .ToArray();
            if (clauses.Length > 1) return clauses;
        }
        return new[] { content };
    }

    private static List<string> Balance(IReadOnlyList<string> parts)
    {
        if (parts.Count <= 3) return parts.ToList();
        int split = (parts.Count + 1) / 2;
        return new List<string>
        {
            string.Join(' ', parts.Take(split)),
            string.Join(' ', parts.Skip(split))
        };
    }

    private static string SimplifyForRepeat(string content, string callsign)
    {
        string value = content;
        if (callsign.Length > 0) value = RemoveLeadingCallsign(value, callsign);
        value = RepeatFillerRegex().Replace(value, string.Empty).TrimStart(' ', ',', '-', '—', ':');
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.Length == 0) value = content;
        return callsign.Length == 0
            ? $"Say again: {value}"
            : $"{callsign}, say again: {value}";
    }

    private static string RemoveLeadingCallsign(string content, string callsign)
    {
        if (callsign.Length == 0) return content;
        return Regex.Replace(
            content,
            $@"^\s*{Regex.Escape(callsign)}\s*[,;:—-]?\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
    }

    private static string NormalizeCallsign(string? callsign)
        => Regex.Replace(callsign ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeText(string? text)
        => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeInput(string? text)
        => NormalizeText(text).TrimEnd('.', '!', '?').ToLowerInvariant();

    private sealed record LastTransmission(
        string Content,
        bool AwaitingCopy,
        DateTimeOffset CreatedAtUtc);

    [GeneratedRegex(@"(?<=[.!?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundaryRegex();

    [GeneratedRegex(@"\s*(?:;|—|\s-\s)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ClauseBoundaryRegex();

    [GeneratedRegex(@"\b(?:and|also|additionally|however|but|then|while|with|near|north|south|east|west|grid|metres?|kilometres?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComplexityRegex();

    [GeneratedRegex(@"\b(?:contact|enemy|hostile|unknown contact|under fire|incoming|urgent|danger|attack|engage|wounded|casualt|reacquired|movement)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OperationalRegex();

    [GeneratedRegex(@"\b(?:contact|enemy|hostile|under fire|incoming|urgent|danger close|attack|engage|wounded|casualt|reacquired)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrgentRegex();

    [GeneratedRegex(@"^(?:copy|copied|roger|affirmative|yes|received|i copy|got it)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AffirmativeRegex();

    [GeneratedRegex(@"^(?:negative|no|did not copy|didn't copy)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegativeRegex();

    [GeneratedRegex(@"^(?:repeat|repeat that|please repeat(?: that)?|say again|say that again|come again|can you repeat(?: that)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatRegex();

    [GeneratedRegex(@"^(?:(?:papa bear[,.]?\s*)?(?:copy|roger|be advised|message received|i have new information|stand by)[,.:—-]?\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepeatFillerRegex();

    [GeneratedRegex(@"^(?:Over|Out)\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RadioTerminatorRegex();

    [GeneratedRegex(@"\s+Over\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingOverRegex();
}
