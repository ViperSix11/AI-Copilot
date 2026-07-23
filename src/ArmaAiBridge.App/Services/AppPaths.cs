namespace ArmaAiBridge.App.Services;

public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArmA AI Bridge");

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");
    public static string LogDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string StateMirrorDirectory { get; } = Path.Combine(RootDirectory, "state");
    public static string StateMirrorDatabase { get; } = Path.Combine(StateMirrorDirectory, "state-mirror.sqlite3");
    public static string MapIntelligenceDatabase { get; } = Path.Combine(StateMirrorDirectory, "map-intelligence.sqlite3");
}
