using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimeTableManager.App;

/// <summary>Modeless snapshot of review items at application time; it never changes the timetable.</summary>
public sealed class TeacherDayReviewWindow : Window
{
    public TeacherDayReviewWindow(string detail)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "배치 후 수작업 확인 목록"; Width = 650; Height = 420; MinWidth = 450; MinHeight = 280;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(14) };
        var guide = new TextBlock { Text = "배치를 반영했습니다. 연강 수업을 확인하고 필요한 수업만 수동 교체하세요.",
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(guide, Dock.Top); panel.Children.Add(guide);
        var note = new TextBlock { Text = "적용 시점의 목록입니다. 이후 조정 결과는 시간표에서 확인하세요. 이 작업 전체는 Ctrl+Z로 되돌릴 수 있습니다.",
            Foreground = Brushes.SlateGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var close = new Button { Content = "닫기", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new TextBox { Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), FontSize = 12 });
        Content = panel;
    }
}
