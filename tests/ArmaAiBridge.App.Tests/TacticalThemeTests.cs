using System.Xml.Linq;
using Xunit;

namespace ArmaAiBridge.App.Tests;

public sealed class TacticalThemeTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void ThemeDictionary_IsValidLocalXamlAndIsLoadedByTheApplication()
    {
        string theme = Read("src", "ArmaAiBridge.App", "Themes", "TacticalTheme.xaml");
        string app = Read("src", "ArmaAiBridge.App", "App.xaml");

        XDocument document = XDocument.Parse(theme);

        Assert.Equal("ResourceDictionary", document.Root?.Name.LocalName);
        Assert.Contains("Source=\"Themes/TacticalTheme.xaml\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Source=\"http", theme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<Image", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void MainShell_ShowsExactBrandVersionAndEveryExistingTab()
    {
        string window = Read("src", "ArmaAiBridge.App", "MainWindow.xaml");
        string dynamicTabs = Read("src", "ArmaAiBridge.App", "MainWindow.Assistant.cs");

        Assert.Contains("Title=\"ArmA AI Bridge - Papa Bear\"", window, StringComparison.Ordinal);
        Assert.Contains("Text=\"ArmA AI Bridge - Papa Bear\"", window, StringComparison.Ordinal);
        Assert.Contains("Text=\"v0.8.1\"", window, StringComparison.Ordinal);
        foreach (string tab in new[] { "Dashboard", "Map query", "API keys", "Logs" })
            Assert.Contains($"Header=\"{tab}\"", window, StringComparison.Ordinal);
        Assert.Contains("Header = \"Assistant\"", dynamicTabs, StringComparison.Ordinal);
        Assert.Contains("Header = \"World State\"", dynamicTabs, StringComparison.Ordinal);
        Assert.Contains("Header = \"AI Context\"", dynamicTabs, StringComparison.Ordinal);
    }

    [Fact]
    public void AiContextTab_IsReadOnlyAndUsesTheExactSharedInterpreterPath()
    {
        string panel = Read("src", "ArmaAiBridge.App", "AiContextPanel.cs");
        string snapshots = Read("src", "ArmaAiBridge.App", "Services", "WorldSnapshotBuilder.cs");
        string assistant = Read("src", "ArmaAiBridge.App", "Services", "OpenAiAssistantService.cs");

        Assert.Contains("IsReadOnly = true", panel, StringComparison.Ordinal);
        Assert.Contains("TryBuildEvidenceContext", panel, StringComparison.Ordinal);
        Assert.Contains("TacticalContextInterpreter.Analyze(snapshot, question)", snapshots, StringComparison.Ordinal);
        Assert.Contains("TacticalContextInterpreter.Interpret(worldSnapshot, question)", assistant, StringComparison.Ordinal);
        Assert.Contains("Prospective question", panel, StringComparison.Ordinal);
        Assert.Contains("Reset AI Context...", panel, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNo", panel, StringComparison.Ordinal);
        Assert.Contains("_stateRepository.ResetCache()", panel, StringComparison.Ordinal);
        Assert.Contains("_snapshots.ResetTacticalContext()", panel, StringComparison.Ordinal);
        Assert.Contains("API keys and response settings are preserved", panel, StringComparison.Ordinal);
        foreach (string stage in new[] { "1. Candidates", "2. Selected", "3. Fused", "4. Transmitted" })
            Assert.Contains(stage, panel, StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_DefinesReadableTerminalAndExplicitInteractionStates()
    {
        string theme = Read("src", "ArmaAiBridge.App", "Themes", "TacticalTheme.xaml");

        foreach (string resource in new[]
        {
            "BrandTitleTextStyle", "SectionHeaderTextStyle", "TerminalTextBoxStyle",
            "TerminalListBoxStyle", "DestructiveButtonStyle", "StatusBadgeStyle"
        })
            Assert.Contains($"x:Key=\"{resource}\"", theme, StringComparison.Ordinal);

        foreach (string state in new[] { "IsMouseOver", "IsPressed", "IsKeyboardFocused", "IsEnabled", "IsSelected" })
            Assert.Contains($"Property=\"{state}\"", theme, StringComparison.Ordinal);

        Assert.Contains("FontSize\" Value=\"13.5\"", theme, StringComparison.Ordinal);
        Assert.Contains("Color=\"#F1F2E8\"", theme, StringComparison.Ordinal);
        Assert.Contains("Color=\"#D9B86A\"", theme, StringComparison.Ordinal);
        Assert.Contains("Color=\"#D07B70\"", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void MainShell_PreservesNamedControlsAndUsesSharedTerminalStyles()
    {
        string window = Read("src", "ArmaAiBridge.App", "MainWindow.xaml");

        foreach (string control in new[]
        {
            "StartButton", "StopButton", "RawTelemetryTextBox", "QueryResultTextBox",
            "SendQueryButton", "OpenAiKeyBox", "ElevenLabsKeyBox", "LogListBox"
        })
            Assert.Contains($"x:Name=\"{control}\"", window, StringComparison.Ordinal);

        Assert.Contains("Style=\"{StaticResource TerminalTextBoxStyle}\"", window, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource TerminalListBoxStyle}\"", window, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepositoryRoot }.Concat(parts).ToArray()));
}
