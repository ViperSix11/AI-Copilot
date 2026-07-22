namespace ArmaAiBridge.App.Models;

public sealed class ResponseProfileSettings
{
    public string Preset { get; set; } = "authentic-military";
    public string Language { get; set; } = "auto";
    public string Length { get; set; } = "short";
    public string Terminator { get; set; } = "none";
    public string CustomTerminator { get; set; } = string.Empty;
    public string CustomStyle { get; set; } = string.Empty;
}
