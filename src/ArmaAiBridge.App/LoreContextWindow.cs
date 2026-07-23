using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ArmaAiBridge.App.Models;
using ArmaAiBridge.App.Services;

namespace ArmaAiBridge.App;

public sealed class LoreContextWindow : Window
{
    private static readonly string[] Scopes = ["Mission", "Map", "Player", "Target", "Common"];
    private readonly IMissionMemoryRepository _repository;
    private readonly Dictionary<string, Editor> _editors = new(StringComparer.Ordinal);

    public LoreContextWindow(IMissionMemoryRepository repository)
    {
        _repository = repository;
        Title = "Lore Context";
        Width = 820; Height = 640; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Build();
        Reload();
    }

    private UIElement Build()
    {
        DockPanel root = new() { Margin = new Thickness(16) };
        TextBlock note = new()
        {
            Text = "Lore is bounded, mission-scoped context. It is supplied to the model as untrusted data, never instructions.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        WrapPanel actions = new() { Margin = new Thickness(0, 10, 0, 0) };
        Button import = new() { Content = "Import JSON" }, export = new() { Content = "Export JSON" };
        import.Click += Import_Click; export.Click += Export_Click;
        actions.Children.Add(import); actions.Children.Add(export);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        TabControl tabs = new();
        foreach (string scope in Scopes)
        {
            TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 2000 };
            CheckBox enabled = new() { Content = "Enabled", IsChecked = true }, always = new() { Content = "Always include", Margin = new Thickness(12, 0, 0, 0) };
            TextBlock count = new() { Margin = new Thickness(12, 0, 0, 0) };
            text.TextChanged += (_, _) => count.Text = $"{text.Text.Length:N0} / 2,000 characters";
            Button save = new() { Content = "Save" }, revert = new() { Content = "Revert" }, preview = new() { Content = "Preview" }, clear = new() { Content = "Clear" };
            Editor editor = new(scope, text, enabled, always, count); _editors.Add(scope, editor);
            save.Click += (_, _) => Save(editor); revert.Click += (_, _) => Reload();
            preview.Click += (_, _) => MessageBox.Show(this, text.Text.Length == 0 ? "This section is empty." : text.Text, $"{scope} lore preview", MessageBoxButton.OK, MessageBoxImage.Information);
            clear.Click += (_, _) => Clear(editor);
            WrapPanel options = new() { Margin = new Thickness(0, 0, 0, 8) };
            options.Children.Add(enabled); options.Children.Add(always); options.Children.Add(count);
            WrapPanel buttons = new() { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(save); buttons.Children.Add(revert); buttons.Children.Add(preview); buttons.Children.Add(clear);
            DockPanel panel = new(); DockPanel.SetDock(options, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(options); panel.Children.Add(buttons); panel.Children.Add(text);
            tabs.Items.Add(new TabItem { Header = scope, Content = panel });
        }
        root.Children.Add(tabs); return root;
    }

    private void Reload()
    {
        Dictionary<string, LoreSection> sections = _repository.GetLoreSections().ToDictionary(x => x.Scope, StringComparer.Ordinal);
        foreach (Editor editor in _editors.Values)
        {
            if (sections.TryGetValue(editor.Scope, out LoreSection? section))
            {
                editor.Text.Text = section.Content; editor.Enabled.IsChecked = section.Enabled; editor.Always.IsChecked = section.AlwaysInclude;
            }
            else { editor.Text.Clear(); editor.Enabled.IsChecked = true; editor.Always.IsChecked = false; }
        }
    }

    private void Save(Editor editor)
    {
        _repository.SaveLoreSection(editor.Scope, editor.Text.Text, editor.Enabled.IsChecked == true, editor.Always.IsChecked == true);
        editor.Count.Text = $"Saved · {editor.Text.Text.Length:N0} / 2,000 characters";
    }

    private void Clear(Editor editor)
    {
        if (MessageBox.Show(this, $"Clear the {editor.Scope} lore section?", "Confirm clear", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _repository.ClearLoreSection(editor.Scope); Reload();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Filter = "JSON files (*.json)|*.json", FileName = "papa-bear-lore.json" };
        if (dialog.ShowDialog(this) != true) return;
        var export = _editors.Values.Select(x => new { scope = x.Scope, content = x.Text.Text, enabled = x.Enabled.IsChecked == true, alwaysInclude = x.Always.IsChecked == true });
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Filter = "JSON files (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Lore import must be a JSON array.");
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string scope = item.GetProperty("scope").GetString() ?? string.Empty;
            if (!_editors.TryGetValue(scope, out Editor? editor)) continue;
            string content = item.GetProperty("content").GetString() ?? string.Empty;
            if (content.Length > 2000) throw new InvalidDataException("A lore section exceeds 2,000 characters.");
            editor.Text.Text = content;
            editor.Enabled.IsChecked = item.TryGetProperty("enabled", out JsonElement enabled) && enabled.GetBoolean();
            editor.Always.IsChecked = item.TryGetProperty("alwaysInclude", out JsonElement always) && always.GetBoolean();
        }
    }

    private sealed record Editor(string Scope, TextBox Text, CheckBox Enabled, CheckBox Always, TextBlock Count);
}
