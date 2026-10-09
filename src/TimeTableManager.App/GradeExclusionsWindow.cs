using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public sealed class GradeExclusionsWindow : Window
{
    public ImmutableArray<GradeDateExclusion> Result { get; private set; } = [];
    public GradeExclusionsWindow(ProjectDocument document, DateOnly? onlyDate = null)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = onlyDate is { } chosen ? $"{chosen:M/d} · 학년 행사 제외" : "날짜별 학년 행사 제외";
        Width = 580; MinWidth = 500;
        if (onlyDate is null) { Height = 560; MinHeight = 350; } else SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var calendar = document.Calendar!;
        var existing = SchoolDayRules.Entries(document).Select(x => (x.Date, x.Grade)).ToHashSet();
        var dates = document.Current.Slots.Select(calendar.DateOf).Concat(existing.Select(x => x.Date))
            .Where(x => x.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (onlyDate is null || x == onlyDate))
            .Distinct().Order().ToArray();
        var panel = new DockPanel { Margin = new Thickness(16) };
        var hint = new TextBlock { Text = "• 제외할 학년 선택(다중 선택 가능)\n• 선택 날짜의 이동·보강·새 배치 차단\n• 다음 주 같은 요일은 별도 설정", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var fields = new List<(DateOnly Date, int Grade, CheckBox Box)>();
        var rows = new StackPanel();
        foreach (var date in dates)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = $"{date:yyyy/M/d} ({"일월화수목금토"[(int)date.DayOfWeek]})", Width = 190, VerticalAlignment = VerticalAlignment.Center });
            for (int grade = 1; grade <= 3; grade++)
            {
                var box = new CheckBox { Content = $"{grade}학년", IsChecked = existing.Contains((date, grade)), Width = 88, VerticalAlignment = VerticalAlignment.Center };
                fields.Add((date, grade, box)); row.Children.Add(box);
            }
            rows.Children.Add(row);
        }
        var footer = new StackPanel();
        var unknown = document.Current.Classes.Where(x => SchoolDayRules.GradeOf(x.Name) is null).Select(x => x.Name).ToArray();
        if (unknown.Length > 0) footer.Children.Add(new TextBlock { Text = "학년 미분류(제외 적용 불가): " + string.Join(", ", unknown) + "\n기초 자료의 학급명을 1-1 또는 1학년 1반 형태로 사용하세요.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var clear = new Button { Content = "모두 해제" }; var cancel = new Button { Content = "취소", IsCancel = true }; var apply = new Button { Content = "제외 설정 적용", IsDefault = true };
        clear.Click += (_, _) => { foreach (var field in fields) field.Box.IsChecked = false; };
        apply.Click += (_, _) =>
        {
            var editedDates = dates.ToHashSet();
            Result = SchoolDayRules.Entries(document).Where(x => !editedDates.Contains(x.Date))
                .Concat(fields.Where(x => x.Box.IsChecked == true).Select(x => new GradeDateExclusion(x.Date, x.Grade)))
                .OrderBy(x => x.Date).ThenBy(x => x.Grade).ToImmutableArray();
            DialogResult = true;
        };
        buttons.Children.Add(clear); buttons.Children.Add(cancel); buttons.Children.Add(apply); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = panel;
    }
}
