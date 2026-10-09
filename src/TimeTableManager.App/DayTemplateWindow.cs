using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;
using TimeTableManager.Domain;

namespace TimeTableManager.App;

public sealed class DayTemplateWindow : Window
{
    private sealed record DateChoice(DateOnly Date, string Label);
    private readonly ComboBox date = new() { DisplayMemberPath = "Label" };
    private readonly ComboBox weekday = new() { DisplayMemberPath = "Label" };
    public DateOnly TargetDate => date.SelectedItem is DateChoice selected ? selected.Date : default;
    public int SourceDay => weekday.SelectedItem is Option selected ? int.Parse(selected.Id) : 0;
    public DayTemplateWindow(ProjectDocument document, DateOnly? selectedDate)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "기초 요일 시간표 적용"; Width = 540; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var calendar = document.Calendar!;
        var periods = DayTemplatePlanner.PeriodCounts(document);
        var dates = document.Current.Slots.Where(x => x.Day <= 5 && calendar.CanEdit(x)).Select(calendar.DateOf).Distinct().Order()
            .Select(x => new DateChoice(x, $"{x:yyyy/M/d} ({"일월화수목금토"[(int)x.DayOfWeek]}) · {periods.GetValueOrDefault(x)}교시")).ToArray();
        date.ItemsSource = dates;
        date.SelectedItem = dates.FirstOrDefault(x => x.Date == selectedDate) ?? dates.FirstOrDefault(x => x.Date == DateOnly.FromDateTime(DateTime.Today)) ?? dates.FirstOrDefault();
        var pattern = DayTemplatePlanner.OriginalPattern(document);
        weekday.ItemsSource = Enumerable.Range(1,5).Where(d => pattern.Slots.Any(x => x.Day == d))
            .Select(d => new Option(d.ToString(), $"기초 {"월화수목금"[d-1]}요일 시간표")).ToArray();
        weekday.SelectedIndex = 0;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "변경할 날짜", FontWeight = FontWeights.SemiBold }); panel.Children.Add(date);
        panel.Children.Add(new TextBlock { Text = "가져올 기초 요일", FontWeight = FontWeights.SemiBold }); panel.Children.Add(weekday);
        panel.Children.Add(new TextBlock { Text = "선택 날짜의 전 학급 수업을 기초 요일로 바꿉니다.\n예: 6교시 월요일에 7교시 목요일 수업 적용.\n필요한 교시는 늘어나며 다른 날짜의 배정은 유지합니다.\n학교 계획에 따른 기초 배정 수정 · 교체 이력에 미포함", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "기초 배정 적용" }; var cancel = new Button { Content = "취소", IsDefault = true, IsCancel = true };
        apply.Click += (_,_) => { if(date.SelectedItem is not null && weekday.SelectedItem is not null) DialogResult = true; };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}
