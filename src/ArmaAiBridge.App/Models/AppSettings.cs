namespace ArmaAiBridge.App.Models;

public sealed class AppSettings
{
    public string OpenAiApiKeyProtected { get; set; } = string.Empty;
    public string ElevenLabsApiKeyProtected { get; set; } = string.Empty;
    public string ElevenLabsVoiceId { get; set; } = string.Empty;
    public ResponseProfileSettings ResponseProfile { get; set; } = new();
}
