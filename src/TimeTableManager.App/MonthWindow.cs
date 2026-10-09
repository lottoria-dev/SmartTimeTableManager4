using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;

namespace TimeTableManager.App;

public sealed class MonthWindow : Window
{
    public DateOnly Month { get; private set; }
    public MonthWindow(DateOnly initial)
    {
        Style = (Style)FindResource(typeof(Window));
        initial = new(Math.Clamp(initial.Year, 1901, 9998), initial.Month, 1);
        Month = MonthlyTimetable.MonthOf(initial);
        Title = "관리 월 선택"; Width = 475; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "기초 시간표를 월 단위로 반복하고, 월 경계 교체를 위해 인접 주도 포함합니다.\n이미 존재하는 날짜의 배정·변경 이력은 그대로 유지합니다.", TextWrapping = TextWrapping.Wrap });
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 4) };
        var years = new ComboBox { Width = 105, ItemsSource = Enumerable.Range(Math.Max(1901, initial.Year - 2), Math.Min(9998, initial.Year + 2) - Math.Max(1901, initial.Year - 2) + 1), SelectedItem = initial.Year };
        var months = new ComboBox { Width = 75, ItemsSource = Enumerable.Range(1, 12), SelectedItem = initial.Month };
        fields.Children.Add(years); fields.Children.Add(new TextBlock { Text = "년  ", VerticalAlignment = VerticalAlignment.Center });
        fields.Children.Add(months); fields.Children.Add(new TextBlock { Text = "월", VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(fields);
        panel.Children.Add(new TextBlock { Text = "다음 달도 이 작업 파일에서 이어서 관리하세요. 서로 다른 월별 파일의 변경을 자동으로 합치지는 않습니다.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkSlateGray });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "확정", IsDefault = true }; var cancel = new Button { Content = "취소", IsCancel = true };
        apply.Click += (_, _) => { if (years.SelectedItem is int year && months.SelectedItem is int month) { Month = new(year, month, 1); DialogResult = true; } };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}
