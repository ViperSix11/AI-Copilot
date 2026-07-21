using System.Text.Json;
using ArmaAiBridge.App.Models;

namespace ArmaAiBridge.App.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.RootDirectory);
        if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
        await using FileStream stream = File.OpenRead(AppPaths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(AppPaths.RootDirectory);
        string temporaryFile = AppPaths.SettingsFile + ".tmp";
        await using (FileStream stream = File.Create(temporaryFile))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryFile, AppPaths.SettingsFile, overwrite: true);
    }
}
