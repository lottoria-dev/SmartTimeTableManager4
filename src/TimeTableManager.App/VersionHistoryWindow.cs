using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

/// <summary>Offline release history, embedded from the same text shipped with the ZIP.</summary>
public sealed class VersionHistoryWindow : Window
{
    public VersionHistoryWindow()
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "버전 히스토리"; Width = 690; Height = 570; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(18) };
        var close = new Button { Content = "닫기", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0,12,0,0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        using var stream = typeof(VersionHistoryWindow).Assembly.GetManifestResourceStream("TimeTableManager.VersionHistory");
        using var reader = stream is null ? null : new StreamReader(stream);
        var body = new TextBlock { Text = reader?.ReadToEnd() ?? "버전 히스토리를 읽을 수 없습니다.", FontSize = 13,
            LineHeight = 23, TextWrapping = TextWrapping.Wrap };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)FindResource("AquaEdge"), BorderThickness = new Thickness(1), Padding = new Thickness(20), Child = scroll });
        Content = panel;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }
}
