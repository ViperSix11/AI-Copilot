using System.Globalization;
using System.Text.RegularExpressions;

namespace ArmaAiBridge.App.Services;

public static partial class RadioSpeechTextNormalizer
{
    private static readonly string[] Ones =
    { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    private static readonly string[] Tens =
    { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };

    public static string Normalize(string visibleAnswer, string? currentGroupCallsign = null)
    {
        string value = CallsignSpeechFormatter.FormatAnswerForSpeech(visibleAnswer, currentGroupCallsign);
        value = UnitValueRegex().Replace(value, match =>
        {
            string words = NumberToWords(match.Groups["number"].Value);
            string unit = ExpandUnit(match.Groups["unit"].Value, match.Groups["number"].Value);
            return $"{words} {unit}";
        });
        value = AcronymRegex().Replace(value, match => match.Value.ToUpperInvariant() switch
        {
            "ASL" => "above sea level",
            "AGL" => "above ground level",
            "ATL" => "above terrain level",
            "MOA" => "minutes of angle",
            "FCS" => "fire-control system",
            _ => match.Value
        });
        value = StandaloneNumberRegex().Replace(value, match => NumberToWords(match.Value));
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    public static string NumberToWords(string token)
    {
        string value = token.Trim();
        bool negative = value.StartsWith("-", StringComparison.Ordinal);
        if (negative) value = value[1..];
        bool leadingDecimal = value.StartsWith(".", StringComparison.Ordinal);
        if (leadingDecimal) value = "0" + value;
        string[] parts = value.Split('.', 2);
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long integer))
            return token;
        string result = leadingDecimal ? string.Empty : IntegerToWords(integer);
        if (parts.Length == 2)
        {
            string decimals = string.Join(' ', parts[1].Select(character => Ones[character - '0']));
            result += (leadingDecimal ? "point " : " point ") + decimals;
        }
        return negative ? "minus " + result : result;
    }

    private static string ExpandUnit(string raw, string number)
    {
        bool singular = decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed) && Math.Abs(parsed) == 1;
        return raw.ToUpperInvariant() switch
        {
            "M/S" => "metres per second",
            "KM/H" => "kilometres per hour",
            "°C" => "degrees Celsius",
            "°" => singular ? "degree" : "degrees",
            "ASL" => "metres above sea level",
            "AGL" => "metres above ground level",
            "ATL" => "metres above terrain level",
            "KM" => singular ? "kilometre" : "kilometres",
            "CM" => singular ? "centimetre" : "centimetres",
            "MM" => singular ? "millimetre" : "millimetres",
            "M" => singular ? "metre" : "metres",
            "MIL" or "MILS" => singular ? "milliradian" : "milliradians",
            "MOA" => "minutes of angle",
            "LM" => "Lapua Magnum",
            _ => raw
        };
    }

    private static string IntegerToWords(long value)
    {
        if (value < 20) return Ones[(int)value];
        if (value < 100) return Tens[(int)(value / 10)] + (value % 10 == 0 ? "" : "-" + Ones[(int)(value % 10)]);
        if (value < 1000) return Ones[(int)(value / 100)] + " hundred" + (value % 100 == 0 ? "" : " " + IntegerToWords(value % 100));
        if (value < 1_000_000) return IntegerToWords(value / 1000) + " thousand" + (value % 1000 == 0 ? "" : " " + IntegerToWords(value % 1000));
        if (value < 1_000_000_000) return IntegerToWords(value / 1_000_000) + " million" + (value % 1_000_000 == 0 ? "" : " " + IntegerToWords(value % 1_000_000));
        return value.ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?<number>-?(?:\d+(?:\.\d+)?|\.\d+))\s*(?<unit>km/h|m/s|°C|°|ASL|AGL|ATL|mils?|MOA|km|cm|mm|m|LM)(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitValueRegex();

    [GeneratedRegex(@"\b(?:ASL|AGL|ATL|MOA|FCS)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcronymRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])[-+]?(?:\d+(?:\.\d+)?|\.\d+)(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneNumberRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
