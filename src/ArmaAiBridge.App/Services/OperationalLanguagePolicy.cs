using System.Text.RegularExpressions;

namespace ArmaAiBridge.App.Services;

/// <summary>Keeps internal state vocabulary out of ordinary radio traffic.</summary>
public static class OperationalLanguagePolicy
{
    private static readonly Regex InternalQuestion = new(
        @"\b(?:backend|database|state mirror|telemetry|prompt|model context|provenance|schema|debug|diagnostic|implementation)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Replacement)[] Replacements =
    {
        Pair(@"\bplayer-reported\b", "reported"),
        Pair(@"\bthe player's\b", "your"),
        Pair(@"\bplayer's\b", "your"),
        Pair(@"\bthe player reports\b", "you report"),
        Pair(@"\bplayer reports(?=\s+(?:indicate|show|say|confirm|describe))\b", "your reports"),
        Pair(@"\bplayer observations\b", "your observations"),
        Pair(@"\bplayer observation\b", "your observation"),
        Pair(@"\bplayer report\b", "your report"),
        Pair(@"\bplayer-provided\b", "reported"),
        Pair(@"\bplayer reports\b", "you report"),
        Pair(@"\bthe player\b", "you"),
        Pair(@"\bplayer\b", "you"),
        Pair(@"\bown-side observations\b", "recent observations"),
        Pair(@"\bown-side\b", "friendly"),
        Pair(@"\bmission-defined\b", "named"),
        Pair(@"\bcanonical\b", "current"),
        Pair(@"\bState Mirror\b", "current picture"),
        Pair(@"\bbounded picture\b", "available picture"),
        Pair(@"\btelemetry feed\b", "current information"),
        Pair(@"\bcontact tracks\b", "contacts"),
        Pair(@"\bcontact track\b", "contact"),
        Pair(@"\bdatabase\b", "records"),
        Pair(@"\bprovenance\b", "source"),
        Pair(@"\bevidence\b", "information"),
        Pair(@"\bfreshness\b", "recency"),
        Pair(@"\bconfidence\b", "certainty")
    };

    public static string Normalize(string answer, string question)
    {
        if (string.IsNullOrWhiteSpace(answer) || InternalQuestion.IsMatch(question ?? string.Empty)) return answer;
        string result = answer;
        foreach ((Regex pattern, string replacement) in Replacements) result = pattern.Replace(result, replacement);
        return Regex.Replace(result, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant).Trim();
    }

    private static (Regex, string) Pair(string pattern, string replacement)
        => (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), replacement);
}
