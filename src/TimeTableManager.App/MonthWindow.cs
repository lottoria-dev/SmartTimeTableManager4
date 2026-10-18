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
        Title = "조회할 월 선택"; Width = 475; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "전체 시간표를 조회할 월을 선택하세요.\n월을 바꾸어도 기존 수업 배정과 변경 이력은 유지합니다.", TextWrapping = TextWrapping.Wrap });
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 4) };
        var years = new ComboBox { Width = 105, ItemsSource = Enumerable.Range(Math.Max(1901, initial.Year - 2), Math.Min(9998, initial.Year + 2) - Math.Max(1901, initial.Year - 2) + 1), SelectedItem = initial.Year };
        var months = new ComboBox { Width = 75, ItemsSource = Enumerable.Range(1, 12), SelectedItem = initial.Month };
        fields.Children.Add(years); fields.Children.Add(new TextBlock { Text = "년  ", VerticalAlignment = VerticalAlignment.Center });
        fields.Children.Add(months); fields.Children.Add(new TextBlock { Text = "월", VerticalAlignment = VerticalAlignment.Center }); panel.Children.Add(fields);
        panel.Children.Add(new TextBlock { Text = "편집·자동 탐색 범위는 오늘부터 선택한 금요일까지입니다. 조회 월을 바꾸어도 작업 기간은 바뀌지 않습니다.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkSlateGray });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "조회", IsDefault = true }; var cancel = new Button { Content = "취소", IsCancel = true };
        apply.Click += (_, _) => { if (years.SelectedItem is int year && months.SelectedItem is int month) { Month = new(year, month, 1); DialogResult = true; } };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}
