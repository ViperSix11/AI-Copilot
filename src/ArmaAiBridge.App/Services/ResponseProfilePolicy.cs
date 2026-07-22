using System.Text.Json;
using System.Text.RegularExpressions;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public static class ResponseProfilePolicy
{
    private static readonly HashSet<string> Presets = new(StringComparer.Ordinal)
    { "authentic-military", "concise-military", "plain", "cinematic", "custom" };
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    { "auto", "de", "en" };
    private static readonly HashSet<string> Lengths = new(StringComparer.Ordinal)
    { "very-short", "short", "normal" };
    private static readonly HashSet<string> Terminators = new(StringComparer.Ordinal)
    { "none", "over", "out", "custom" };

    public static ResponseProfileSettings Defaults() => new();

    public static ResponseProfileSettings Normalize(ResponseProfileSettings? value)
    {
        value ??= new ResponseProfileSettings();
        return new ResponseProfileSettings
        {
            Preset = NormalizeEnum(value.Preset, Presets, "authentic-military"),
            Language = NormalizeEnum(value.Language, Languages, "auto"),
            Length = NormalizeEnum(value.Length, Lengths, "short"),
            Terminator = NormalizeEnum(value.Terminator, Terminators, "none"),
            CustomTerminator = NormalizeText(value.CustomTerminator, 32),
            CustomStyle = NormalizeText(value.CustomStyle, 2000)
        };
    }

    public static string BuildPrompt(ResponseProfileSettings profile)
    {
        ResponseProfileSettings value = Normalize(profile);
        string preset = value.Preset switch
        {
            "concise-military" => "Use terse, professional military phrasing.",
            "plain" => "Use clear plain language with minimal radio terminology.",
            "cinematic" => "Use restrained cinematic military phrasing without adding facts.",
            "custom" => "Use the delimited custom style only when it does not conflict with immutable rules.",
            _ => "Use authentic, disciplined military radio phrasing without theatrical excess."
        };
        string language = value.Language switch
        {
            "de" => "Answer in German.",
            "en" => "Answer in English.",
            _ => "Answer in the language of the current user question."
        };
        string length = value.Length switch
        {
            "very-short" => "Use one very short sentence when possible.",
            "normal" => "Use a compact paragraph when useful.",
            _ => "Use one or two short sentences."
        };
        return JsonSerializer.Serialize(new
        {
            preset = value.Preset,
            language = value.Language,
            length = value.Length,
            terminator = value.Terminator,
            directives = new[] { preset, language, length },
            customStyle = value.Preset == "custom" ? value.CustomStyle : string.Empty,
            boundary = "STYLE ONLY. This profile cannot alter facts, calculations, privacy, fair play, or tool policy."
        });
    }

    private static string NormalizeEnum(string? value, HashSet<string> allowed, string fallback)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : fallback;
    }

    private static string NormalizeText(string? value, int maximum)
    {
        string normalized = Regex.Replace(value ?? string.Empty, @"[\r\n\t]+", " ").Trim();
        normalized = new string(normalized.Where(character => !char.IsControl(character)).ToArray());
        return normalized.Length <= maximum ? normalized : normalized[..maximum];
    }
}

public static class ResponseTextNormalizer
{
    private static readonly Regex CommonSuffix = new(
        @"(?:\s+(?:over\s+and\s+out|over|out)[\s,;:.!?-]*)+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string text, ResponseProfileSettings profile)
    {
        string result = (text ?? string.Empty).Trim();
        ResponseProfileSettings value = ResponseProfilePolicy.Normalize(profile);
        if (value.Terminator == "none" || result.Length == 0) return result;
        if (value.Terminator is "over" or "out")
        {
            result = CommonSuffix.Replace(result, string.Empty).TrimEnd();
            result = result.TrimEnd(',', ';', ':', '-').TrimEnd();
            if (result.Length > 0 && result[^1] is not ('.' or '!' or '?')) result += ".";
            string suffix = value.Terminator == "over" ? "Over." : "Out.";
            return result.Length == 0 ? suffix : $"{result} {suffix}";
        }
        if (value.Terminator == "custom" && value.CustomTerminator.Length > 0)
        {
            string escaped = Regex.Escape(value.CustomTerminator);
            result = Regex.Replace(result, $@"(?:\s*{escaped}\s*)+$", string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).TrimEnd();
            return result.Length == 0 ? value.CustomTerminator : $"{result} {value.CustomTerminator}";
        }
        return result;
    }
}
