using System.Windows;
using System.Windows.Controls;
using TimeTableManager.Application;

namespace TimeTableManager.App;

public sealed class TeacherDayWindow : Window
{
    public int MorningLastPeriod { get; private set; } = 4;
    public SwapDirection Direction { get; private set; } = SwapDirection.Later;
    public bool SpillOverflow { get; private set; }
    public TeacherDayWindow(string teacher, DateOnly date, TeacherDayGoal goal, int maxPeriod)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "교사 하루 수업 자동 이동"; Width = 540; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        bool relocating = goal is TeacherDayGoal.OtherDates or TeacherDayGoal.ClearMorning;
        var label = goal == TeacherDayGoal.Morning ? "1교시부터 앞으로 몰기" : goal == TeacherDayGoal.Afternoon ? "마지막 교시부터 뒤로 몰기" : goal == TeacherDayGoal.ClearMorning ? "오전 수업만 다른 날짜로 분산" : "하루 수업 모두 다른 날짜로 분산";
        panel.Children.Add(new TextBlock { Text = $"{teacher} · {date:yyyy/M/d}\n{label}", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,10) });
        var spill = new CheckBox { Content = "시간대 초과 수업은 가까운 다른 날짜로 이동", Margin = new Thickness(0,6,0,6), Visibility = relocating ? Visibility.Collapsed : Visibility.Visible };
        var boundary = new ComboBox { DisplayMemberPath = "Label", ItemsSource = Enumerable.Range(1,Math.Max(1,maxPeriod-1))
            .Select(p => new Option(p.ToString(),$"오전 마지막 {p}교시 · 오후 {p+1}교시부터")).ToArray(), SelectedIndex = Math.Min(3,Math.Max(0,maxPeriod-2)), IsEnabled = goal == TeacherDayGoal.ClearMorning };
        panel.Children.Add(spill);
        if (goal != TeacherDayGoal.OtherDates) panel.Children.Add(boundary);
        var directions = new ComboBox { DisplayMemberPath = "Label", ItemsSource = new[]{new Option("Later","이후 날짜"),new Option("Earlier","이전 날짜"),new Option("Both","이전·이후 날짜")}, SelectedIndex = 0,
            Visibility = relocating ? Visibility.Visible : Visibility.Collapsed };
        spill.Checked += (_,_) => { boundary.IsEnabled = true; directions.Visibility = Visibility.Visible; };
        spill.Unchecked += (_,_) => { boundary.IsEnabled = goal == TeacherDayGoal.ClearMorning; directions.Visibility = Visibility.Collapsed; };
        panel.Children.Add(directions);
        panel.Children.Add(new TextBlock { Text = relocating
            ? "배치 우선 → 연강 확인 → 필요한 수업만 수작업 조정.\n가까운 날짜에서 함께 이동하는 수업 수가 적은 안을 우선합니다.\n3·4연강 이상도 배치안에 포함될 수 있으며 교사·교시·교과를 확인합니다.\n오전만 비우기는 설정한 오전까지 이동하며 오후 수업은 유지합니다."
            : "대상 교사의 연강은 기본 허용합니다.\n3시간이면 오전 1·2·3교시, 6교시 종료일의 오후는 4·5·6교시가 기본입니다.\n초과 이동을 켜면 점심 경계를 넘는 수업은 인접 날짜로 옮깁니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8) });
        panel.Children.Add(new TextBlock { Text = relocating ? "적용 후 수작업 확인 목록 제공. 잠금·중복 배정·교사 부재·행사 제외는 계속 검사합니다. 전체 배치가 완성된 안만 제시합니다."
            : "함께 바뀌는 다른 교사의 새 3연강은 회피·확인합니다. 전체 배치가 완성된 안만 제시합니다.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.SlateGray });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        var ok = new Button { Content = "AI 변경안 찾기" }; var cancel = new Button { Content = "취소", IsDefault = true, IsCancel = true };
        ok.Click += (_,_) =>
        {
            SpillOverflow = spill.IsChecked == true;
            if (boundary.SelectedItem is Option b) MorningLastPeriod = int.Parse(b.Id);
            Direction = relocating || SpillOverflow
                ? (directions.SelectedItem is Option selected ? Enum.Parse<SwapDirection>(selected.Id) : SwapDirection.Later) : SwapDirection.SameDay;
            DialogResult = true;
        };
        actions.Children.Add(ok); actions.Children.Add(cancel); panel.Children.Add(actions); Content = panel;
    }
}
