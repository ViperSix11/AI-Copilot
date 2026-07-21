namespace ArmaAiBridge.App.Services;

public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArmA AI Bridge");

    public static string SettingsFile { get; } = Path.Combine(RootDirectory, "settings.json");
    public static string LogDirectory { get; } = Path.Combine(RootDirectory, "logs");
}
