using System.Windows;

namespace ArmaAiBridge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventHandler? activated = null;
        activated = (_, _) =>
        {
            if (Current.MainWindow is not ArmaAiBridge.App.MainWindow main) return;
            Activated -= activated;
            main.AttachAssistantTab();
        };
        Activated += activated;
    }
}
