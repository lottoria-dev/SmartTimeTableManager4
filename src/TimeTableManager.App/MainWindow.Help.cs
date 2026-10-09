using System.Windows;

namespace TimeTableManager.App;

public partial class MainWindow
{
    private HelpWindow? helpWindow;
    private AboutWindow? aboutWindow;
    private VersionHistoryWindow? versionHistoryWindow;
    private void ShowHelp(int topic)
    {
        if (helpWindow is null)
        {
            var window = new HelpWindow { Owner = this }; helpWindow = window;
            window.Closed += (_, _) => { if (ReferenceEquals(helpWindow, window)) helpWindow = null; };
            window.SelectTopic(topic); window.Show();
        }
        else { helpWindow.SelectTopic(topic); if (helpWindow.WindowState == WindowState.Minimized) helpWindow.WindowState = WindowState.Normal; helpWindow.Activate(); }
    }
    private void HelpClick(object sender, RoutedEventArgs e) => ShowHelp(0);
    private void ModeHelpClick(object sender, RoutedEventArgs e) => ShowHelp(1 + (int)workMode);
    private void ScenariosHelpClick(object sender, RoutedEventArgs e) => ShowHelp(12);
    private void ShortcutsHelpClick(object sender, RoutedEventArgs e) => ShowHelp(10);
    private void VersionHistoryClick(object sender, RoutedEventArgs e)
    {
        if (versionHistoryWindow is not null)
        {
            if (versionHistoryWindow.WindowState == WindowState.Minimized) versionHistoryWindow.WindowState = WindowState.Normal;
            versionHistoryWindow.Activate(); return;
        }
        var window = new VersionHistoryWindow { Owner = this }; versionHistoryWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(versionHistoryWindow, window)) versionHistoryWindow = null; };
        window.Show();
    }
    private void AboutClick(object sender, RoutedEventArgs e)
    {
        if (aboutWindow is not null) { aboutWindow.Activate(); return; }
        var window = new AboutWindow { Owner = this }; aboutWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(aboutWindow, window)) aboutWindow = null; };
        window.Show();
    }
}
