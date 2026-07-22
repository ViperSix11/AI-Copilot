using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArmaAiBridge.App;

public partial class MainWindow
{
    private AssistantPanel? _assistantPanel;
    private WorldStateDiagnosticsPanel? _worldStateDiagnosticsPanel;
    private MapKnowledgeDiagnosticsPanel? _mapKnowledgeDiagnosticsPanel;

    internal void AttachAssistantTab()
    {
        if (_assistantPanel is not null) return;
        TabControl? tabs = FindVisualChild<TabControl>(this);
        if (tabs is null) return;
        _assistantPanel = new AssistantPanel(
            _pipeServer, _settingsService, _log, _worldSnapshotBuilder,
            _mapKnowledgeSnapshotBuilder);
        _worldStateDiagnosticsPanel = new WorldStateDiagnosticsPanel(
            _worldStateStore, _worldSnapshotBuilder);
        tabs.Items.Insert(1, new TabItem { Header = "Assistant", Content = _assistantPanel });
        tabs.Items.Insert(2, new TabItem { Header = "World State", Content = _worldStateDiagnosticsPanel });
        _mapKnowledgeDiagnosticsPanel = new MapKnowledgeDiagnosticsPanel(_mapKnowledgeService);
        tabs.Items.Insert(3, new TabItem { Header = "Map Knowledge", Content = _mapKnowledgeDiagnosticsPanel });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            T? nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
