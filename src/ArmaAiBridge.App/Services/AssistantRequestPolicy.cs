namespace ArmaAiBridge.App.Services;

public static class AssistantRequestPolicy
{
    private static readonly string[] ClearlyNonOperationalTerms =
    {
        "bake", "bread", "recipe", "cake", "poem", "joke", "movie review",
        "stock price", "sports score", "personal email", "write a story"
    };

    private static readonly string[] TerrainQueryTerms =
    {
        "terrain object", "terrain objects", "building nearby", "buildings nearby", "buildings are nearby",
        "building ahead", "buildings ahead", "road ahead", "roads ahead",
        "vegetation ahead", "trees ahead", "wall ahead", "walls ahead",
        "rock ahead", "rocks ahead", "cover nearby", "cover ahead",
        "what is ahead", "what's ahead", "what is nearby", "what's nearby"
    };

    public static bool IsOperational(string question)
    {
        string normalized = Normalize(question);
        return normalized.Length > 0 && !ClearlyNonOperationalTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    public static bool RequiresTerrainObjectTool(string question)
    {
        string normalized = Normalize(question);
        return TerrainQueryTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    private static string Normalize(string value)
        => " " + (value ?? string.Empty).Trim().ToLowerInvariant() + " ";
}
