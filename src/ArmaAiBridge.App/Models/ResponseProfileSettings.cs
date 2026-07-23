namespace ArmaAiBridge.App.Models;

public sealed class ResponseProfileSettings
{
    public const string DefaultOperatorPrePrompt = "Read the player input first. Use only context directly relevant to the answer or a necessary clarification. Do not recite unrelated context.";
    public string Preset { get; set; } = "authentic-military";
    public string Language { get; set; } = "auto";
    public string Length { get; set; } = "short";
    public string Terminator { get; set; } = "none";
    public string CustomTerminator { get; set; } = string.Empty;
    public string CustomStyle { get; set; } = string.Empty;
    public string Banter { get; set; } = "dry";
    public string OperatorPrePrompt { get; set; } = DefaultOperatorPrePrompt;
}
